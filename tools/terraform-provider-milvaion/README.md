# Terraform Provider for Milvaion

Manage [Milvaion](https://github.com/Milvasoft/milvaion) scheduled jobs with Terraform.

- Resource `milvaion_job`: create, update, delete and import jobs.
- Data source `milvaion_job`: look a job up by id or name.
- Data source `milvaion_worker`: read a registered worker and the job types it offers.

User guide: [Jobs as Code](../../docs/portaldocs/29-jobs-as-code.md). Attribute reference: [`docs/`](docs/). Examples: [`examples/`](examples/).

```hcl
provider "milvaion" {
  url = "https://milvaion.example.com" # or MILVAION_URL
  # api_key from MILVAION_API_KEY
}

resource "milvaion_job" "report" {
  name            = "nightly-report"
  worker_id       = "reporter-worker"
  job_name        = "ReportJob"
  cron_expression = "0 0 2 * * *"
  job_data        = jsonencode({ format = "pdf" })
}
```

## Development

The provider is built with [terraform-plugin-framework](https://developer.hashicorp.com/terraform/plugin/framework) and reuses the API client and job-data normalisation from [`../milvaion-cli`](../milvaion-cli) through a `replace` directive in `go.mod`.

```bash
go test ./...       # unit tests: run Terraform against an in-memory fake API
go build .
```

The unit tests need a `terraform` binary. If none is on `PATH`, terraform-plugin-testing downloads one.

### Acceptance Tests

Acceptance tests create and delete real jobs, named `tf-acc-*`. They are skipped unless `TF_ACC` is set:

```bash
export TF_ACC=1
export MILVAION_ACC_URL=http://localhost:5000
export MILVAION_ACC_API_KEY=...            # ScheduledJobManagement.* and WorkerManagement.List/Detail
export MILVAION_ACC_WORKER_ID=sample-worker
export MILVAION_ACC_JOB_NAME=TestJob
go test ./... -run TestAcc -v
```

Start a local stack with the repository's `docker-compose.yml` (it includes `sample-worker`), then create a key with [`tools/scripts/create-dev-api-key.py`](../scripts/create-dev-api-key.py) or in the dashboard.

### Using a Local Build

```hcl
# ~/.terraformrc
provider_installation {
  dev_overrides {
    "milvasoft/milvaion" = "/path/to/milvaion/tools/terraform-provider-milvaion"
  }
  direct {}
}
```

Then `go build .` and run `terraform plan` in a configuration that uses the provider (skip `terraform init`).

## Releasing

Push a `terraform-provider-vX.Y.Z` tag. [`terraform-provider-release.yml`](../../.github/workflows/terraform-provider-release.yml) runs the tests, builds the provider for every platform the Terraform Registry serves, signs `SHA256SUMS` with GPG and attaches the zips, the checksums, the signature and the registry manifest to a GitHub release of that tag.

### One-Time Setup (Manual)

These steps need an account owner and are not automated.

**1. Create a signing key.** The Registry accepts RSA and DSA keys, not the ECC default of newer GnuPG:

```bash
gpg --full-generate-key          # choose "RSA and RSA", 4096 bits
gpg --list-secret-keys --keyid-format=long
gpg --armor --export-secret-keys KEY_ID > private.asc
gpg --armor --export KEY_ID > public.asc
```

**2. Add the GitHub secrets** in the repository settings, under Secrets and variables → Actions:

| Secret | Value |
|--------|-------|
| `TERRAFORM_PROVIDER_GPG_PRIVATE_KEY` | contents of `private.asc` |
| `TERRAFORM_PROVIDER_GPG_PASSPHRASE` | the key's passphrase |

Delete `private.asc` afterwards.

**3. Publish to a registry.** The public Terraform Registry only reads providers from a GitHub repository named `terraform-provider-<name>`, with the release assets at its top level. This provider lives in a subdirectory of the Milvaion repository, so pick one:

- **Public Terraform Registry.** Create the repository `Milvasoft/terraform-provider-milvaion`. For each release, create a release with the same `vX.Y.Z` tag there and upload the assets from this repository's `terraform-provider-vX.Y.Z` release (`gh release download terraform-provider-vX.Y.Z -R Milvasoft/milvaion` then `gh release create vX.Y.Z -R Milvasoft/terraform-provider-milvaion *`). Then, once: sign in to [registry.terraform.io](https://registry.terraform.io) with GitHub, add `public.asc` under **Settings → Signing Keys** for the `milvasoft` namespace, and choose **Publish → Provider** with that repository. The Registry picks up later releases through its webhook.
- **HCP Terraform private registry.** Upload the same assets and the public key through the [private registry API](https://developer.hashicorp.com/terraform/cloud-docs/registry/publish-providers). No mirror repository is needed.
- **Network mirror or filesystem mirror.** Serve the zips from any HTTPS server or unpack them into `terraform.d/plugins`. Signatures are not checked.

The provider address in `main.go` is `registry.terraform.io/milvasoft/milvaion`. If you publish under another namespace, change it there and in the examples.

**4. Registry docs.** The Registry renders [`docs/`](docs/) from the published repository. Copy it into the mirror repository with each release.
