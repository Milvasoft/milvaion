# milvaion CLI

Manage [Milvaion](https://github.com/Milvasoft/milvaion) jobs from the command line and from YAML files kept in Git.

```bash
milvaion login --url https://milvaion.example.com --api-key "$KEY"
milvaion export > jobs.yaml          # bring existing jobs under version control
milvaion diff -f jobs.yaml           # what would change
milvaion apply -f jobs.yaml --prune  # make the server match the file
milvaion trigger nightly-report --follow
```

User guide: [Jobs as Code](../../docs/portaldocs/29-jobs-as-code.md).

## Commands

| Command | What it does |
|---------|--------------|
| `login` | Check and save the URL and api key. `MILVAION_URL` / `MILVAION_API_KEY` override it. |
| `apply -f FILE` | Create or update jobs to match the files. `--prune` deletes managed jobs missing from them. `--allow-replace` permits delete-and-recreate for fields the API cannot change in place. |
| `diff -f FILE` | Show what `apply` would do. `--exit-code` exits 2 when there are changes. |
| `export` | Write current jobs as `milvaion/v1` YAML. |
| `get jobs\|workers\|occurrences` | List things. `-o table\|json\|yaml`. |
| `trigger JOB` | Run a job now. `--follow` streams its logs. |
| `logs OCCURRENCE` | Show an occurrence's logs. `--job NAME` for the newest run, `-f` to follow. |
| `schema` | List the fields of the YAML schema. |

## The YAML Schema

```yaml
apiVersion: milvaion/v1
kind: Job                 # or JobList with items:
metadata:
  name: nightly-report    # display name; the key apply matches on
spec:
  worker: reporter-worker
  jobName: ReportJob
  cron: "0 0 2 * * *"     # six fields, seconds first
  jobData: {format: pdf}
```

The schema lives in [`pkg/spec/fields.go`](pkg/spec/fields.go) as a table with one entry per field. Each entry names the YAML key and the matching properties of the create command, the update command and the job detail response. When the API gains a job field, add one entry:

```go
{Name: "timeZone", Kind: KString, Create: "timeZone", Update: "timeZone", Detail: "timeZone",
	Doc: "IANA time zone the cron expression is evaluated in."},
```

Parsing, validation, diffing, `apply`, `export` and `milvaion schema` all pick it up. Leave `Update` empty if the API cannot change the field in place; `apply` then replaces the job when it changes. A breaking change to the schema means a new `apiVersion`.

## Layout

| Path | Contents |
|------|----------|
| `pkg/client` | REST client for the jobs, occurrences and workers endpoints. The Terraform provider uses it too. |
| `pkg/spec` | The `milvaion/v1` schema, normalisation and the plan `apply` and `diff` act on. |
| `pkg/fakeapi` | In-memory fake of the API for unit tests. |
| `internal/cmd` | The commands. |

## Development

```bash
go test ./...
go build -o milvaion .
```

The end-to-end test runs against a real Milvaion and is skipped by default:

```bash
MILVAION_E2E=1 MILVAION_E2E_URL=http://localhost:5000 MILVAION_E2E_API_KEY=... \
MILVAION_E2E_WORKER_ID=sample-worker MILVAION_E2E_JOB_NAME=TestJob go test ./internal/cmd -run TestE2E
```

[`tools/scripts/create-dev-api-key.py`](../scripts/create-dev-api-key.py) creates a suitable key on a local stack from the root password.

## Releasing

Push a `cli-vX.Y.Z` tag. [`cli-release.yml`](../../.github/workflows/cli-release.yml) runs the tests, builds Linux, macOS and Windows binaries (amd64 and arm64) with GoReleaser and attaches them to a GitHub release of that tag.
