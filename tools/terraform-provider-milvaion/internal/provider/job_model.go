package provider

import (
	"context"
	"encoding/json"
	"fmt"
	"sort"
	"strings"
	"time"

	"github.com/hashicorp/terraform-plugin-framework/attr"
	"github.com/hashicorp/terraform-plugin-framework/diag"
	"github.com/hashicorp/terraform-plugin-framework/types"
	"github.com/hashicorp/terraform-plugin-framework/types/basetypes"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/spec"
)

// jobModel is the state of milvaion_job and of the milvaion_job data source.
type jobModel struct {
	ID                        types.String `tfsdk:"id"`
	Name                      types.String `tfsdk:"name"`
	Description               types.String `tfsdk:"description"`
	Tags                      types.Set    `tfsdk:"tags"`
	WorkerID                  types.String `tfsdk:"worker_id"`
	JobName                   types.String `tfsdk:"job_name"`
	CronExpression            types.String `tfsdk:"cron_expression"`
	ExecuteAt                 types.String `tfsdk:"execute_at"`
	IsActive                  types.Bool   `tfsdk:"is_active"`
	ConcurrentExecutionPolicy types.String `tfsdk:"concurrent_execution_policy"`
	JobData                   types.String `tfsdk:"job_data"`
	ZombieTimeoutMinutes      types.Int64  `tfsdk:"zombie_timeout_minutes"`
	ExecutionTimeoutSeconds   types.Int64  `tfsdk:"execution_timeout_seconds"`
	AutoDisable               types.Object `tfsdk:"auto_disable"`
	IsExternal                types.Bool   `tfsdk:"is_external"`
	ExternalJobID             types.String `tfsdk:"external_job_id"`
}

type autoDisableModel struct {
	Enabled              types.Bool  `tfsdk:"enabled"`
	Threshold            types.Int64 `tfsdk:"threshold"`
	FailureWindowMinutes types.Int64 `tfsdk:"failure_window_minutes"`
}

var autoDisableAttrTypes = map[string]attr.Type{
	"enabled":                types.BoolType,
	"threshold":              types.Int64Type,
	"failure_window_minutes": types.Int64Type,
}

// canonicalJobData returns the canonical JSON of a job_data value, so two
// payloads that differ only in whitespace or key order compare equal.
func canonicalJobData(s string) (string, error) {
	f, _ := spec.FieldByName("jobData")
	v, err := spec.Normalize(f, s)
	if err != nil {
		return "", err
	}
	if v == nil {
		return "", nil
	}
	return v.(string), nil
}

func jobDataEqual(a, b string) bool {
	ca, errA := canonicalJobData(a)
	cb, errB := canonicalJobData(b)
	return errA == nil && errB == nil && ca == cb
}

func (m *jobModel) tagList(ctx context.Context) ([]string, diag.Diagnostics) {
	var tags []string
	if m.Tags.IsNull() || m.Tags.IsUnknown() {
		return nil, nil
	}
	diags := m.Tags.ElementsAs(ctx, &tags, false)
	sort.Strings(tags)
	return tags, diags
}

func (m *jobModel) autoDisable(ctx context.Context) (*autoDisableModel, diag.Diagnostics) {
	if m.AutoDisable.IsNull() || m.AutoDisable.IsUnknown() {
		return nil, nil
	}
	var ad autoDisableModel
	diags := m.AutoDisable.As(ctx, &ad, basetypes.ObjectAsOptions{})
	return &ad, diags
}

func autoDisableBody(ad *autoDisableModel) map[string]any {
	enabled := true
	if !ad.Enabled.IsNull() && !ad.Enabled.IsUnknown() {
		enabled = ad.Enabled.ValueBool()
	}
	return map[string]any{
		"enabled":              enabled,
		"threshold":            int64OrNil(ad.Threshold),
		"failureWindowMinutes": int64OrNil(ad.FailureWindowMinutes),
	}
}

func stringOrNil(v types.String) any {
	if v.IsNull() || v.IsUnknown() || v.ValueString() == "" {
		return nil
	}
	return v.ValueString()
}

func int64OrNil(v types.Int64) any {
	if v.IsNull() || v.IsUnknown() {
		return nil
	}
	return v.ValueInt64()
}

// createBody builds the CreateScheduledJobCommand for a planned job.
func (m *jobModel) createBody(ctx context.Context) (map[string]any, diag.Diagnostics) {
	var diags diag.Diagnostics
	tags, d := m.tagList(ctx)
	diags.Append(d...)
	policy, err := client.EnumValue(client.ConcurrencyPolicies, m.ConcurrentExecutionPolicy.ValueString())
	if err != nil {
		diags.AddError("Invalid concurrent_execution_policy", err.Error())
	}
	body := map[string]any{
		"displayName":               m.Name.ValueString(),
		"description":               stringOrNil(m.Description),
		"tags":                      strings.Join(tags, ","),
		"workerId":                  stringOrNil(m.WorkerID),
		"selectedJobName":           stringOrNil(m.JobName),
		"cronExpression":            stringOrNil(m.CronExpression),
		"isActive":                  m.IsActive.ValueBool(),
		"concurrentExecutionPolicy": policy,
		"zombieTimeoutMinutes":      int64OrNil(m.ZombieTimeoutMinutes),
		"executionTimeoutSeconds":   int64OrNil(m.ExecutionTimeoutSeconds),
		"isExternal":                m.IsExternal.ValueBool(),
		"externalJobId":             stringOrNil(m.ExternalJobID),
	}
	if at := stringOrNil(m.ExecuteAt); at != nil {
		body["executeAt"] = at
	}
	if jd := stringOrNil(m.JobData); jd != nil {
		canonical, err := canonicalJobData(jd.(string))
		if err != nil {
			diags.AddError("Invalid job_data", err.Error())
		}
		body["jobData"] = canonical
	}
	ad, d := m.autoDisable(ctx)
	diags.Append(d...)
	if ad != nil {
		body["autoDisableSettings"] = autoDisableBody(ad)
	}
	return body, diags
}

// updateChanges returns the UpdateScheduledJobCommand properties that differ between plan and state.
func updateChanges(ctx context.Context, plan, state *jobModel) (map[string]any, diag.Diagnostics) {
	var diags diag.Diagnostics
	changes := map[string]any{}
	if !plan.Name.Equal(state.Name) {
		changes["displayName"] = plan.Name.ValueString()
	}
	if !plan.Description.Equal(state.Description) {
		changes["description"] = stringOrNil(plan.Description)
	}
	if !plan.Tags.Equal(state.Tags) {
		tags, d := plan.tagList(ctx)
		diags.Append(d...)
		changes["tags"] = strings.Join(tags, ",")
	}
	if !plan.JobName.Equal(state.JobName) {
		changes["jobType"] = stringOrNil(plan.JobName)
	}
	if !plan.CronExpression.Equal(state.CronExpression) {
		changes["cronExpression"] = stringOrNil(plan.CronExpression)
	}
	if !plan.IsActive.Equal(state.IsActive) {
		changes["isActive"] = plan.IsActive.ValueBool()
	}
	if !plan.ConcurrentExecutionPolicy.Equal(state.ConcurrentExecutionPolicy) {
		v, err := client.EnumValue(client.ConcurrencyPolicies, plan.ConcurrentExecutionPolicy.ValueString())
		if err != nil {
			diags.AddError("Invalid concurrent_execution_policy", err.Error())
		}
		changes["concurrentExecutionPolicy"] = v
	}
	if !plan.JobData.Equal(state.JobData) && !jobDataEqual(plan.JobData.ValueString(), state.JobData.ValueString()) {
		canonical, err := canonicalJobData(plan.JobData.ValueString())
		if err != nil {
			diags.AddError("Invalid job_data", err.Error())
		}
		if canonical == "" {
			canonical = "{}"
		}
		changes["jobData"] = canonical
	}
	if !plan.ZombieTimeoutMinutes.Equal(state.ZombieTimeoutMinutes) {
		changes["zombieTimeoutMinutes"] = int64OrNil(plan.ZombieTimeoutMinutes)
	}
	if !plan.ExecutionTimeoutSeconds.Equal(state.ExecutionTimeoutSeconds) {
		changes["executionTimeoutSeconds"] = int64OrNil(plan.ExecutionTimeoutSeconds)
	}
	// Removing auto_disable stops managing it; it does not reset the server's settings.
	if !plan.AutoDisable.IsNull() && !plan.AutoDisable.Equal(state.AutoDisable) {
		ad, d := plan.autoDisable(ctx)
		diags.Append(d...)
		if ad != nil {
			changes["autoDisableSettings"] = autoDisableBody(ad)
		}
	}
	return changes, diags
}

// readFrom copies a job returned by the API into m. Values that are equivalent
// to what m already holds keep m's spelling, so a refresh does not show drift
// for formatting differences. With full, every attribute is filled from the
// server (import and data source); otherwise attributes that m leaves
// unmanaged (auto_disable, execute_at of recurring jobs) stay as they are.
func (m *jobModel) readFrom(ctx context.Context, job *client.Job, full bool) diag.Diagnostics {
	var diags diag.Diagnostics
	m.ID = types.StringValue(job.ID)
	m.Name = types.StringValue(job.DisplayName)
	m.Description = keepString(m.Description, job.Description)
	m.WorkerID = keepString(m.WorkerID, job.WorkerID)
	m.JobName = keepString(m.JobName, job.JobType)
	m.CronExpression = keepString(m.CronExpression, job.CronExpression)
	m.IsActive = types.BoolValue(job.IsActive)
	m.ConcurrentExecutionPolicy = types.StringValue(job.ConcurrencyPolicy())
	m.ZombieTimeoutMinutes = intValue(job.ZombieTimeoutMinutes)
	m.ExecutionTimeoutSeconds = intValue(job.ExecutionTimeoutSeconds)

	tags := job.TagList()
	if len(tags) == 0 {
		// An explicit empty set stays empty; otherwise no tags is null.
		if !full && !m.Tags.IsNull() && !m.Tags.IsUnknown() {
			m.Tags = types.SetValueMust(types.StringType, []attr.Value{})
		} else {
			m.Tags = types.SetNull(types.StringType)
		}
	} else {
		values := make([]attr.Value, len(tags))
		for i, t := range tags {
			values[i] = types.StringValue(t)
		}
		set, d := types.SetValue(types.StringType, values)
		diags.Append(d...)
		m.Tags = set
	}

	canonical, err := canonicalJobData(job.JobData)
	switch {
	case err != nil:
		m.JobData = types.StringValue(job.JobData)
	case canonical == "":
		if !m.JobData.IsNull() && !m.JobData.IsUnknown() && jobDataEqual(m.JobData.ValueString(), "") {
			// Keep "{}" or "" as written.
		} else {
			m.JobData = types.StringNull()
		}
	case !m.JobData.IsNull() && !m.JobData.IsUnknown() && jobDataEqual(m.JobData.ValueString(), canonical):
		// Same payload, keep the configured formatting.
	default:
		m.JobData = types.StringValue(canonical)
	}

	external := job.ExternalJobInfo != nil && job.ExternalJobInfo.IsExternal
	m.IsExternal = types.BoolValue(external)
	extID := ""
	if job.ExternalJobInfo != nil {
		extID = job.ExternalJobInfo.ExternalJobID
	}
	m.ExternalJobID = keepString(m.ExternalJobID, extID)

	// The API moves executeAt to the next run of a recurring job, so it is only
	// read back for one-time jobs.
	if job.CronExpression == "" && job.ExecuteAt != nil && !external {
		serverAt := job.ExecuteAt.UTC()
		prior, perr := time.Parse(time.RFC3339, m.ExecuteAt.ValueString())
		if m.ExecuteAt.IsNull() || m.ExecuteAt.IsUnknown() || perr != nil || !prior.Equal(serverAt) {
			m.ExecuteAt = types.StringValue(serverAt.Format(time.RFC3339))
		}
	} else if full || m.ExecuteAt.IsUnknown() {
		m.ExecuteAt = types.StringNull()
	}

	if full || (!m.AutoDisable.IsNull() && !m.AutoDisable.IsUnknown()) {
		obj, d := autoDisableObject(job.AutoDisableSettings)
		diags.Append(d...)
		m.AutoDisable = obj
	} else if m.AutoDisable.IsUnknown() {
		m.AutoDisable = types.ObjectNull(autoDisableAttrTypes)
	}
	_ = ctx
	return diags
}

func autoDisableObject(s *client.AutoDisableSettings) (types.Object, diag.Diagnostics) {
	enabled := true
	var threshold, window types.Int64 = types.Int64Null(), types.Int64Null()
	if s != nil {
		if s.Enabled != nil {
			enabled = *s.Enabled
		}
		threshold = intValue(s.Threshold)
		window = intValue(s.FailureWindowMinutes)
	}
	return types.ObjectValue(autoDisableAttrTypes, map[string]attr.Value{
		"enabled":                types.BoolValue(enabled),
		"threshold":              threshold,
		"failure_window_minutes": window,
	})
}

// keepString maps an empty server value to null unless the prior value was an explicit "".
func keepString(prior types.String, server string) types.String {
	if server == "" {
		if !prior.IsNull() && !prior.IsUnknown() && prior.ValueString() == "" {
			return prior
		}
		return types.StringNull()
	}
	return types.StringValue(server)
}

func intValue(v *int) types.Int64 {
	if v == nil {
		return types.Int64Null()
	}
	return types.Int64Value(int64(*v))
}

// validateSchedule checks the rules the API enforces, so mistakes show up at plan time.
func (m *jobModel) validateSchedule() []string {
	var problems []string
	if m.IsExternal.ValueBool() {
		if m.ExternalJobID.IsNull() && !m.ExternalJobID.IsUnknown() {
			problems = append(problems, "external_job_id is required when is_external is true")
		}
		return problems
	}
	if m.CronExpression.IsNull() && m.ExecuteAt.IsNull() {
		problems = append(problems, "set cron_expression for a recurring job or execute_at for a one-time job")
	}
	if !m.CronExpression.IsNull() && !m.CronExpression.IsUnknown() {
		if n := len(strings.Fields(m.CronExpression.ValueString())); n != 6 {
			problems = append(problems, fmt.Sprintf("cron_expression has %d fields; Milvaion cron expressions have six, seconds first (\"0 0 2 * * *\" is 02:00 daily)", n))
		}
	}
	if !m.ExecuteAt.IsNull() && !m.ExecuteAt.IsUnknown() {
		if _, err := time.Parse(time.RFC3339, m.ExecuteAt.ValueString()); err != nil {
			problems = append(problems, "execute_at must be an RFC 3339 time such as 2030-01-31T09:00:00Z")
		}
	}
	if !m.JobData.IsNull() && !m.JobData.IsUnknown() && !json.Valid([]byte(m.JobData.ValueString())) && strings.TrimSpace(m.JobData.ValueString()) != "" {
		problems = append(problems, "job_data must be valid JSON; use jsonencode({...})")
	}
	return problems
}
