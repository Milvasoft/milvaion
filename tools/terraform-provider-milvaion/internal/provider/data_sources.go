package provider

import (
	"context"
	"fmt"
	"sort"
	"strings"

	"github.com/hashicorp/terraform-plugin-framework-validators/datasourcevalidator"
	"github.com/hashicorp/terraform-plugin-framework/attr"
	"github.com/hashicorp/terraform-plugin-framework/datasource"
	"github.com/hashicorp/terraform-plugin-framework/datasource/schema"
	"github.com/hashicorp/terraform-plugin-framework/path"
	"github.com/hashicorp/terraform-plugin-framework/types"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
)

func clientFrom(data any) (*client.Client, error) {
	if data == nil {
		return nil, nil
	}
	c, ok := data.(*client.Client)
	if !ok {
		return nil, fmt.Errorf("expected *client.Client, got %T", data)
	}
	return c, nil
}

// --- milvaion_job ---

var _ datasource.DataSourceWithConfigValidators = (*jobDataSource)(nil)

type jobDataSource struct{ client *client.Client }

func newJobDataSource() datasource.DataSource { return &jobDataSource{} }

func (d *jobDataSource) Metadata(_ context.Context, req datasource.MetadataRequest, resp *datasource.MetadataResponse) {
	resp.TypeName = req.ProviderTypeName + "_job"
}

func (d *jobDataSource) Schema(_ context.Context, _ datasource.SchemaRequest, resp *datasource.SchemaResponse) {
	computed := func(desc string) schema.StringAttribute {
		return schema.StringAttribute{Computed: true, Description: desc}
	}
	resp.Schema = schema.Schema{
		Description: "Look up a job by id or by display name.",
		Attributes: map[string]schema.Attribute{
			"id":                          schema.StringAttribute{Optional: true, Computed: true, Description: "Job id. Set id or name."},
			"name":                        schema.StringAttribute{Optional: true, Computed: true, Description: "Display name. Must match exactly one job."},
			"description":                 computed("Description."),
			"tags":                        schema.SetAttribute{Computed: true, ElementType: types.StringType, Description: "Tags."},
			"worker_id":                   computed("Worker that runs the job."),
			"job_name":                    computed("Job type on the worker."),
			"cron_expression":             computed("Cron expression, empty for one-time jobs."),
			"execute_at":                  computed("Time of a one-time run."),
			"is_active":                   schema.BoolAttribute{Computed: true, Description: "Whether the job is dispatched."},
			"concurrent_execution_policy": computed("Skip or Queue."),
			"job_data":                    computed("JSON payload."),
			"zombie_timeout_minutes":      schema.Int64Attribute{Computed: true, Description: "Queued timeout in minutes."},
			"execution_timeout_seconds":   schema.Int64Attribute{Computed: true, Description: "Execution timeout in seconds."},
			"auto_disable": schema.SingleNestedAttribute{
				Computed: true, Description: "Auto-disable settings.",
				Attributes: map[string]schema.Attribute{
					"enabled":                schema.BoolAttribute{Computed: true},
					"threshold":              schema.Int64Attribute{Computed: true},
					"failure_window_minutes": schema.Int64Attribute{Computed: true},
				},
			},
			"is_external":     schema.BoolAttribute{Computed: true, Description: "Reported by an external scheduler."},
			"external_job_id": computed("Identifier in the external scheduler."),
		},
	}
}

func (d *jobDataSource) ConfigValidators(context.Context) []datasource.ConfigValidator {
	return []datasource.ConfigValidator{
		datasourcevalidator.ExactlyOneOf(path.MatchRoot("id"), path.MatchRoot("name")),
	}
}

func (d *jobDataSource) Configure(_ context.Context, req datasource.ConfigureRequest, resp *datasource.ConfigureResponse) {
	c, err := clientFrom(req.ProviderData)
	if err != nil {
		resp.Diagnostics.AddError("Unexpected provider data", err.Error())
		return
	}
	d.client = c
}

func (d *jobDataSource) Read(ctx context.Context, req datasource.ReadRequest, resp *datasource.ReadResponse) {
	var cfg jobModel
	resp.Diagnostics.Append(req.Config.Get(ctx, &cfg)...)
	if resp.Diagnostics.HasError() {
		return
	}
	id := cfg.ID.ValueString()
	if id == "" {
		rows, err := d.client.FindJobsByName(ctx, cfg.Name.ValueString())
		if err != nil {
			resp.Diagnostics.AddError("Looking up job failed", err.Error())
			return
		}
		switch len(rows) {
		case 0:
			resp.Diagnostics.AddError("Job not found", fmt.Sprintf("no job is named %q", cfg.Name.ValueString()))
			return
		case 1:
			id = rows[0].ID
		default:
			ids := make([]string, len(rows))
			for i, r := range rows {
				ids[i] = r.ID
			}
			resp.Diagnostics.AddError("Job name is ambiguous",
				fmt.Sprintf("%d jobs are named %q (%s); look it up by id instead", len(rows), cfg.Name.ValueString(), strings.Join(ids, ", ")))
			return
		}
	}
	job, err := d.client.GetJob(ctx, id)
	if err != nil {
		resp.Diagnostics.AddError("Reading job failed", err.Error())
		return
	}
	state := jobModel{
		Tags: types.SetNull(types.StringType), AutoDisable: types.ObjectNull(autoDisableAttrTypes),
	}
	resp.Diagnostics.Append(state.readFrom(ctx, job, true)...)
	resp.Diagnostics.Append(resp.State.Set(ctx, &state)...)
}

// --- milvaion_worker ---

type workerDataSource struct{ client *client.Client }

func newWorkerDataSource() datasource.DataSource { return &workerDataSource{} }

type workerModel struct {
	WorkerID        types.String `tfsdk:"worker_id"`
	DisplayName     types.String `tfsdk:"display_name"`
	Status          types.String `tfsdk:"status"`
	Version         types.String `tfsdk:"version"`
	JobNames        types.List   `tfsdk:"job_names"`
	RoutingPatterns types.Map    `tfsdk:"routing_patterns"`
	InstanceCount   types.Int64  `tfsdk:"instance_count"`
	CurrentJobs     types.Int64  `tfsdk:"current_jobs"`
}

func (d *workerDataSource) Metadata(_ context.Context, req datasource.MetadataRequest, resp *datasource.MetadataResponse) {
	resp.TypeName = req.ProviderTypeName + "_worker"
}

func (d *workerDataSource) Schema(_ context.Context, _ datasource.SchemaRequest, resp *datasource.SchemaResponse) {
	resp.Schema = schema.Schema{
		Description: "Read a registered worker, for example to check that it offers a job type before scheduling it.",
		Attributes: map[string]schema.Attribute{
			"worker_id":        schema.StringAttribute{Required: true, Description: "Worker id."},
			"display_name":     schema.StringAttribute{Computed: true, Description: "Display name."},
			"status":           schema.StringAttribute{Computed: true, Description: "Active, Inactive or Zombie."},
			"version":          schema.StringAttribute{Computed: true, Description: "Worker version."},
			"job_names":        schema.ListAttribute{Computed: true, ElementType: types.StringType, Description: "Job types the worker can run, sorted."},
			"routing_patterns": schema.MapAttribute{Computed: true, ElementType: types.StringType, Description: "Routing pattern per job type."},
			"instance_count":   schema.Int64Attribute{Computed: true, Description: "Number of running instances."},
			"current_jobs":     schema.Int64Attribute{Computed: true, Description: "Jobs running now across all instances."},
		},
	}
}

func (d *workerDataSource) Configure(_ context.Context, req datasource.ConfigureRequest, resp *datasource.ConfigureResponse) {
	c, err := clientFrom(req.ProviderData)
	if err != nil {
		resp.Diagnostics.AddError("Unexpected provider data", err.Error())
		return
	}
	d.client = c
}

func (d *workerDataSource) Read(ctx context.Context, req datasource.ReadRequest, resp *datasource.ReadResponse) {
	var m workerModel
	resp.Diagnostics.Append(req.Config.Get(ctx, &m)...)
	if resp.Diagnostics.HasError() {
		return
	}
	w, err := d.client.GetWorker(ctx, m.WorkerID.ValueString())
	if client.IsNotFound(err) {
		resp.Diagnostics.AddError("Worker not found",
			fmt.Sprintf("no worker %q is registered; workers register themselves when they start", m.WorkerID.ValueString()))
		return
	}
	if err != nil {
		resp.Diagnostics.AddError("Reading worker failed", err.Error())
		return
	}
	names := append([]string(nil), w.JobNames...)
	sort.Strings(names)
	nameValues := make([]attr.Value, len(names))
	for i, n := range names {
		nameValues[i] = types.StringValue(n)
	}
	patterns := map[string]attr.Value{}
	for k, v := range w.RoutingPatterns {
		patterns[k] = types.StringValue(v)
	}
	m.DisplayName = types.StringValue(w.DisplayName)
	m.Status = types.StringValue(w.Status)
	m.Version = types.StringValue(w.Version)
	m.JobNames = types.ListValueMust(types.StringType, nameValues)
	m.RoutingPatterns = types.MapValueMust(types.StringType, patterns)
	m.InstanceCount = types.Int64Value(int64(len(w.Instances)))
	m.CurrentJobs = types.Int64Value(int64(w.CurrentJobs))
	resp.Diagnostics.Append(resp.State.Set(ctx, &m)...)
}
