// Package spec defines the milvaion/v1 YAML schema for jobs, converts it to and
// from the API's shapes, and computes the plan that `apply` and `diff` act on.
package spec

import (
	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
)

// Kind is the value type of a spec field. It decides how a value is read from
// YAML, normalised for comparison, sent to the API and printed.
type Kind int

const (
	// KString is a single line string.
	KString Kind = iota
	// KInt is a whole number.
	KInt
	// KBool is true or false.
	KBool
	// KTags is a set of strings; the API stores it comma separated and order does not matter.
	KTags
	// KList is an ordered list of strings, sent to the API as a JSON array.
	KList
	// KJSON is any YAML value, sent to the API as a JSON string.
	KJSON
	// KEnum is one of Field.Enum, sent to the API as its index.
	KEnum
	// KTime is an RFC 3339 timestamp, normalised to UTC.
	KTime
	// KAutoDisable is the autoDisable object.
	KAutoDisable
)

// Field describes one key under `spec:` and how it maps onto the API.
//
// Adding a job field the API has gained is one entry in Fields. For example a
// time zone that the create and update commands accept as `timeZone` and the
// job detail returns as `timeZone`:
//
//	{Name: "timeZone", Kind: KString, Create: "timeZone", Update: "timeZone", Detail: "timeZone"},
type Field struct {
	// Name is the YAML key under spec.
	Name string
	// Kind is the value type.
	Kind Kind
	// Enum lists the allowed names for KEnum, in the API's numeric order.
	Enum []string
	// Create is the CreateScheduledJobCommand property, or "" if it cannot be set on create.
	Create string
	// Update is the UpdateScheduledJobCommand property. "" means the API cannot change
	// it in place, so a change replaces the job (delete and create).
	Update string
	// Detail is the ScheduledJobDetailDto property the current value is read from.
	Detail string
	// Read reads the current value when it is not a top-level detail property.
	Read func(job *client.Job) any
	// Default is the value assumed when the field is omitted, and the value the
	// server is assumed to hold when it returns nothing. Export leaves it out.
	Default any
	// OmitMeansUnmanaged leaves the server's value alone when the field is omitted,
	// instead of resetting it to Default.
	OmitMeansUnmanaged bool
	// Skip reports whether the field should not be compared for this spec.
	Skip func(desired map[string]any) bool
	// Doc is a one-line description, used by `milvaion schema`.
	Doc string
}

// Fields is the milvaion/v1 job schema, in the order fields are printed.
var Fields = []Field{
	{Name: "description", Kind: KString, Create: "description", Update: "description", Detail: "description",
		Doc: "Free text shown in the dashboard."},
	{Name: "tags", Kind: KTags, Create: "tags", Update: "tags", Detail: "tags",
		Doc: "List of tags."},
	{Name: "worker", Kind: KString, Create: "workerId", Detail: "workerId",
		Doc: "Worker id that runs the job. Changing it replaces the job."},
	{Name: "jobName", Kind: KString, Create: "selectedJobName", Update: "jobType", Detail: "jobType",
		Doc: "Job type on the worker, e.g. SendEmailJob."},
	{Name: "cron", Kind: KString, Create: "cronExpression", Update: "cronExpression", Detail: "cronExpression",
		Doc: "Six-field cron expression, seconds first: \"0 0 2 * * *\" runs at 02:00 every day."},
	{Name: "executeAt", Kind: KTime, Create: "executeAt", Detail: "executeAt",
		Skip: func(d map[string]any) bool { return d["cron"] != nil || d["external"] == true },
		Doc:  "UTC time of a one-time run. Ignored when cron is set. Changing it replaces the job."},
	{Name: "active", Kind: KBool, Create: "isActive", Update: "isActive", Detail: "isActive", Default: true,
		Doc: "Whether the scheduler dispatches the job. Default true."},
	{Name: "concurrencyPolicy", Kind: KEnum, Enum: client.ConcurrencyPolicies,
		Create: "concurrentExecutionPolicy", Update: "concurrentExecutionPolicy", Detail: "concurrentExecutionPolicy", Default: "Skip",
		Doc: "What happens when a run is due while the previous one is still running: Skip or Queue. Default Skip."},
	{Name: "jobData", Kind: KJSON, Create: "jobData", Update: "jobData", Detail: "jobData",
		Doc: "Payload passed to the job. Any YAML value; sent as JSON."},
	{Name: "zombieTimeoutMinutes", Kind: KInt, Create: "zombieTimeoutMinutes", Update: "zombieTimeoutMinutes", Detail: "zombieTimeoutMinutes",
		Doc: "Minutes a run may stay queued before it is marked failed. Empty uses the server default."},
	{Name: "executionTimeoutSeconds", Kind: KInt, Create: "executionTimeoutSeconds", Update: "executionTimeoutSeconds", Detail: "executionTimeoutSeconds",
		Doc: "Seconds a run may take before the worker cancels it. Empty uses the worker default."},
	{Name: "autoDisable", Kind: KAutoDisable, Create: "autoDisableSettings", Update: "autoDisableSettings", Detail: "autoDisableSettings",
		OmitMeansUnmanaged: true, Default: map[string]any{"enabled": true, "threshold": nil, "failureWindowMinutes": nil},
		Doc: "{enabled, threshold, failureWindowMinutes}: switch the job off after repeated failures. Omit to leave the server's settings alone."},
	{Name: "external", Kind: KBool, Create: "isExternal", Default: false,
		Read: func(j *client.Job) any { return j.ExternalJobInfo != nil && j.ExternalJobInfo.IsExternal },
		Doc:  "The job runs in an external scheduler (Quartz, Hangfire) and only reports to Milvaion."},
	{Name: "externalJobId", Kind: KString, Create: "externalJobId",
		Read: func(j *client.Job) any {
			if j.ExternalJobInfo == nil {
				return nil
			}
			return j.ExternalJobInfo.ExternalJobID
		},
		Doc: "Identifier of the job in the external scheduler. Required when external is true."},
}

// FieldByName returns the field with the given YAML name.
func FieldByName(name string) (*Field, bool) {
	for i := range Fields {
		if Fields[i].Name == name {
			return &Fields[i], true
		}
	}
	return nil, false
}
