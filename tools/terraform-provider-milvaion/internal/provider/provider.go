// Package provider implements the Milvaion Terraform provider.
package provider

import (
	"context"
	"os"

	"github.com/hashicorp/terraform-plugin-framework/datasource"
	"github.com/hashicorp/terraform-plugin-framework/path"
	"github.com/hashicorp/terraform-plugin-framework/provider"
	"github.com/hashicorp/terraform-plugin-framework/provider/schema"
	"github.com/hashicorp/terraform-plugin-framework/resource"
	"github.com/hashicorp/terraform-plugin-framework/types"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
)

var _ provider.Provider = (*milvaionProvider)(nil)

type milvaionProvider struct {
	version string
}

// New returns a provider factory.
func New(version string) func() provider.Provider {
	return func() provider.Provider { return &milvaionProvider{version: version} }
}

type providerModel struct {
	URL    types.String `tfsdk:"url"`
	APIKey types.String `tfsdk:"api_key"`
}

func (p *milvaionProvider) Metadata(_ context.Context, _ provider.MetadataRequest, resp *provider.MetadataResponse) {
	resp.TypeName = "milvaion"
	resp.Version = p.version
}

func (p *milvaionProvider) Schema(_ context.Context, _ provider.SchemaRequest, resp *provider.SchemaResponse) {
	resp.Schema = schema.Schema{
		Description: "Manage Milvaion scheduled jobs and read workers.",
		Attributes: map[string]schema.Attribute{
			"url": schema.StringAttribute{
				Optional:    true,
				Description: "Milvaion address, including any base path, e.g. https://milvaion.example.com. Defaults to the MILVAION_URL environment variable.",
			},
			"api_key": schema.StringAttribute{
				Optional:    true,
				Sensitive:   true,
				Description: "Api key created under User Management > Api Keys. Defaults to the MILVAION_API_KEY environment variable.",
			},
		},
	}
}

func (p *milvaionProvider) Configure(ctx context.Context, req provider.ConfigureRequest, resp *provider.ConfigureResponse) {
	var cfg providerModel
	resp.Diagnostics.Append(req.Config.Get(ctx, &cfg)...)
	if resp.Diagnostics.HasError() {
		return
	}
	if cfg.URL.IsUnknown() || cfg.APIKey.IsUnknown() {
		// Values come from resources that are not created yet; resources will be planned with unknown values.
		return
	}
	url := os.Getenv("MILVAION_URL")
	if !cfg.URL.IsNull() {
		url = cfg.URL.ValueString()
	}
	key := os.Getenv("MILVAION_API_KEY")
	if !cfg.APIKey.IsNull() {
		key = cfg.APIKey.ValueString()
	}
	if url == "" {
		resp.Diagnostics.AddAttributeError(path.Root("url"), "Missing Milvaion URL",
			"Set url in the provider block or the MILVAION_URL environment variable.")
	}
	if key == "" {
		resp.Diagnostics.AddAttributeError(path.Root("api_key"), "Missing Milvaion api key",
			"Set api_key in the provider block or the MILVAION_API_KEY environment variable.")
	}
	if resp.Diagnostics.HasError() {
		return
	}
	c := client.New(url, key)
	c.UserAgent = "terraform-provider-milvaion/" + p.version
	resp.ResourceData = c
	resp.DataSourceData = c
}

func (p *milvaionProvider) Resources(context.Context) []func() resource.Resource {
	return []func() resource.Resource{newJobResource}
}

func (p *milvaionProvider) DataSources(context.Context) []func() datasource.DataSource {
	return []func() datasource.DataSource{newJobDataSource, newWorkerDataSource}
}
