package provider

import (
	"context"
	"fmt"

	"github.com/hashicorp/terraform-plugin-framework-validators/int64validator"
	"github.com/hashicorp/terraform-plugin-framework-validators/stringvalidator"
	"github.com/hashicorp/terraform-plugin-framework/path"
	"github.com/hashicorp/terraform-plugin-framework/resource"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/booldefault"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/boolplanmodifier"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/planmodifier"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/stringdefault"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/stringplanmodifier"
	"github.com/hashicorp/terraform-plugin-framework/schema/validator"
	"github.com/hashicorp/terraform-plugin-framework/types"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
)

var (
	_ resource.Resource                   = (*jobResource)(nil)
	_ resource.ResourceWithConfigure      = (*jobResource)(nil)
	_ resource.ResourceWithImportState    = (*jobResource)(nil)
	_ resource.ResourceWithValidateConfig = (*jobResource)(nil)
)

type jobResource struct {
	client *client.Client
}

func newJobResource() resource.Resource { return &jobResource{} }

func (r *jobResource) Metadata(_ context.Context, req resource.MetadataRequest, resp *resource.MetadataResponse) {
	resp.TypeName = req.ProviderTypeName + "_job"
}

func (r *jobResource) Schema(_ context.Context, _ resource.SchemaRequest, resp *resource.SchemaResponse) {
	resp.Schema = schema.Schema{
		Description: "A Milvaion scheduled job.",
		Attributes: map[string]schema.Attribute{
			"id": schema.StringAttribute{
				Computed:      true,
				Description:   "Job id.",
				PlanModifiers: []planmodifier.String{stringplanmodifier.UseStateForUnknown()},
			},
			"name": schema.StringAttribute{
				Required:    true,
				Description: "Display name.",
				Validators:  []validator.String{stringvalidator.LengthAtLeast(1)},
			},
			"description": schema.StringAttribute{Optional: true, Description: "Free text shown in the dashboard."},
			"tags": schema.SetAttribute{
				Optional: true, ElementType: types.StringType,
				Description: "Tags. They must not contain commas.",
				Validators:  nil,
			},
			"worker_id": schema.StringAttribute{
				Optional:      true,
				Description:   "Worker that runs the job. Changing it replaces the job.",
				PlanModifiers: []planmodifier.String{stringplanmodifier.RequiresReplace()},
			},
			"job_name": schema.StringAttribute{Optional: true, Description: "Job type on the worker, e.g. SendEmailJob."},
			"cron_expression": schema.StringAttribute{
				Optional:    true,
				Description: "Six-field cron expression, seconds first: \"0 0 2 * * *\" runs at 02:00 every day.",
			},
			"execute_at": schema.StringAttribute{
				Optional:      true,
				Description:   "RFC 3339 time of a one-time run. Ignored when cron_expression is set. Changing it replaces the job.",
				PlanModifiers: []planmodifier.String{stringplanmodifier.RequiresReplace()},
			},
			"is_active": schema.BoolAttribute{
				Optional: true, Computed: true, Default: booldefault.StaticBool(true),
				Description: "Whether the scheduler dispatches the job. Defaults to true.",
			},
			"concurrent_execution_policy": schema.StringAttribute{
				Optional: true, Computed: true, Default: stringdefault.StaticString("Skip"),
				Description: "What happens when a run is due while the previous one is still running: Skip or Queue. Defaults to Skip.",
				Validators:  []validator.String{stringvalidator.OneOf(client.ConcurrencyPolicies...)},
			},
			"job_data": schema.StringAttribute{
				Optional:    true,
				Description: "JSON payload passed to the job. Use jsonencode({...}).",
			},
			"zombie_timeout_minutes": schema.Int64Attribute{
				Optional: true, Description: "Minutes a run may stay queued before it is marked failed. Unset uses the server default.",
				Validators: []validator.Int64{int64validator.AtLeast(1)},
			},
			"execution_timeout_seconds": schema.Int64Attribute{
				Optional: true, Description: "Seconds a run may take before the worker cancels it. Unset uses the worker default.",
				Validators: []validator.Int64{int64validator.AtLeast(1)},
			},
			"auto_disable": schema.SingleNestedAttribute{
				Optional:    true,
				Description: "Switch the job off after repeated failures. Leave it out to keep whatever the server has.",
				Attributes: map[string]schema.Attribute{
					"enabled": schema.BoolAttribute{
						Optional: true, Computed: true, Default: booldefault.StaticBool(true),
						Description: "Defaults to true.",
					},
					"threshold": schema.Int64Attribute{
						Optional: true, Description: "Failures in the window that disable the job. Unset uses the server default.",
						Validators: []validator.Int64{int64validator.AtLeast(1)},
					},
					"failure_window_minutes": schema.Int64Attribute{
						Optional: true, Description: "Window the failures are counted in. Unset uses the server default.",
						Validators: []validator.Int64{int64validator.AtLeast(1)},
					},
				},
			},
			"is_external": schema.BoolAttribute{
				Optional: true, Computed: true, Default: booldefault.StaticBool(false),
				Description:   "The job runs in an external scheduler (Quartz, Hangfire) and only reports to Milvaion. Changing it replaces the job.",
				PlanModifiers: []planmodifier.Bool{boolplanmodifier.RequiresReplace()},
			},
			"external_job_id": schema.StringAttribute{
				Optional:      true,
				Description:   "Identifier in the external scheduler. Required when is_external is true. Changing it replaces the job.",
				PlanModifiers: []planmodifier.String{stringplanmodifier.RequiresReplace()},
			},
		},
	}
}

func (r *jobResource) Configure(_ context.Context, req resource.ConfigureRequest, resp *resource.ConfigureResponse) {
	if req.ProviderData == nil {
		return
	}
	c, ok := req.ProviderData.(*client.Client)
	if !ok {
		resp.Diagnostics.AddError("Unexpected provider data", fmt.Sprintf("expected *client.Client, got %T", req.ProviderData))
		return
	}
	r.client = c
}

func (r *jobResource) ValidateConfig(ctx context.Context, req resource.ValidateConfigRequest, resp *resource.ValidateConfigResponse) {
	var m jobModel
	resp.Diagnostics.Append(req.Config.Get(ctx, &m)...)
	if resp.Diagnostics.HasError() {
		return
	}
	if m.CronExpression.IsUnknown() || m.ExecuteAt.IsUnknown() || m.IsExternal.IsUnknown() {
		return
	}
	for _, p := range m.validateSchedule() {
		resp.Diagnostics.AddError("Invalid job schedule", p)
	}
}

func (r *jobResource) Create(ctx context.Context, req resource.CreateRequest, resp *resource.CreateResponse) {
	var plan jobModel
	resp.Diagnostics.Append(req.Plan.Get(ctx, &plan)...)
	if resp.Diagnostics.HasError() {
		return
	}
	body, diags := plan.createBody(ctx)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}
	id, err := r.client.CreateJob(ctx, body)
	if err != nil {
		resp.Diagnostics.AddError("Creating job failed", err.Error())
		return
	}
	job, err := r.client.GetJob(ctx, id)
	if err != nil {
		resp.Diagnostics.AddError("Reading the created job failed", fmt.Sprintf("job %s was created but could not be read back: %v", id, err))
		// Save the id so the next apply does not create a duplicate.
		plan.ID = types.StringValue(id)
		resp.Diagnostics.Append(resp.State.Set(ctx, &plan)...)
		return
	}
	resp.Diagnostics.Append(plan.readFrom(ctx, job, false)...)
	resp.Diagnostics.Append(resp.State.Set(ctx, &plan)...)
}

func (r *jobResource) Read(ctx context.Context, req resource.ReadRequest, resp *resource.ReadResponse) {
	var state jobModel
	resp.Diagnostics.Append(req.State.Get(ctx, &state)...)
	if resp.Diagnostics.HasError() {
		return
	}
	job, err := r.client.GetJob(ctx, state.ID.ValueString())
	if client.IsNotFound(err) {
		resp.State.RemoveResource(ctx)
		return
	}
	if err != nil {
		resp.Diagnostics.AddError("Reading job failed", err.Error())
		return
	}
	// After an import only the id is known: read every attribute.
	imported := state.Name.IsNull()
	resp.Diagnostics.Append(state.readFrom(ctx, job, imported)...)
	resp.Diagnostics.Append(resp.State.Set(ctx, &state)...)
}

func (r *jobResource) Update(ctx context.Context, req resource.UpdateRequest, resp *resource.UpdateResponse) {
	var plan, state jobModel
	resp.Diagnostics.Append(req.Plan.Get(ctx, &plan)...)
	resp.Diagnostics.Append(req.State.Get(ctx, &state)...)
	if resp.Diagnostics.HasError() {
		return
	}
	changes, diags := updateChanges(ctx, &plan, &state)
	resp.Diagnostics.Append(diags...)
	if resp.Diagnostics.HasError() {
		return
	}
	if len(changes) > 0 {
		if err := r.client.UpdateJob(ctx, state.ID.ValueString(), changes); err != nil {
			resp.Diagnostics.AddError("Updating job failed", err.Error())
			return
		}
	}
	job, err := r.client.GetJob(ctx, state.ID.ValueString())
	if err != nil {
		resp.Diagnostics.AddError("Reading the updated job failed", err.Error())
		return
	}
	plan.ID = state.ID
	resp.Diagnostics.Append(plan.readFrom(ctx, job, false)...)
	resp.Diagnostics.Append(resp.State.Set(ctx, &plan)...)
}

func (r *jobResource) Delete(ctx context.Context, req resource.DeleteRequest, resp *resource.DeleteResponse) {
	var state jobModel
	resp.Diagnostics.Append(req.State.Get(ctx, &state)...)
	if resp.Diagnostics.HasError() {
		return
	}
	if _, err := r.client.GetJob(ctx, state.ID.ValueString()); client.IsNotFound(err) {
		return
	}
	if err := r.client.DeleteJob(ctx, state.ID.ValueString()); err != nil {
		resp.Diagnostics.AddError("Deleting job failed", err.Error())
	}
}

func (r *jobResource) ImportState(ctx context.Context, req resource.ImportStateRequest, resp *resource.ImportStateResponse) {
	resource.ImportStatePassthroughID(ctx, path.Root("id"), req, resp)
}
