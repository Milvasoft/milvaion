package provider

import (
	"fmt"
	"os"
	"regexp"
	"testing"

	"github.com/hashicorp/terraform-plugin-framework/providerserver"
	"github.com/hashicorp/terraform-plugin-go/tfprotov6"
	"github.com/hashicorp/terraform-plugin-testing/helper/resource"
	"github.com/hashicorp/terraform-plugin-testing/terraform"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/fakeapi"
)

var protoV6Factories = map[string]func() (tfprotov6.ProviderServer, error){
	"milvaion": providerserver.NewProtocol6WithError(New("test")()),
}

// These tests run Terraform against an in-memory fake of the Milvaion API. They
// need no Milvaion and no TF_ACC; terraform-plugin-testing downloads a
// Terraform binary when none is on PATH.

func providerBlock(url string) string {
	return fmt.Sprintf(`
provider "milvaion" {
  url     = %q
  api_key = %q
}
`, url, fakeapi.APIKey)
}

func TestJobResourceLifecycle(t *testing.T) {
	srv := fakeapi.New()
	defer srv.Close()

	jobDetail := func(field string, want any) resource.TestCheckFunc {
		return func(s *terraform.State) error {
			id := s.RootModule().Resources["milvaion_job.test"].Primary.ID
			job, ok := srv.Jobs[id]
			if !ok {
				return fmt.Errorf("job %s not on the server", id)
			}
			if fmt.Sprint(job[field]) != fmt.Sprint(want) {
				return fmt.Errorf("server %s = %v, want %v", field, job[field], want)
			}
			return nil
		}
	}

	resource.UnitTest(t, resource.TestCase{
		ProtoV6ProviderFactories: protoV6Factories,
		Steps: []resource.TestStep{
			{
				Config: providerBlock(srv.URL) + `
resource "milvaion_job" "test" {
  name            = "tf-test"
  description     = "managed by terraform"
  tags            = ["b", "a"]
  worker_id       = "test-worker"
  job_name        = "TestJob"
  cron_expression = "0 */5 * * * *"
  job_data        = jsonencode({ greeting = "hello", count = 1 })
  auto_disable = {
    threshold = 3
  }
}

data "milvaion_job" "by_name" {
  name       = milvaion_job.test.name
  depends_on = [milvaion_job.test]
}

data "milvaion_worker" "w" {
  worker_id = "test-worker"
}
`,
				Check: resource.ComposeAggregateTestCheckFunc(
					resource.TestCheckResourceAttrSet("milvaion_job.test", "id"),
					resource.TestCheckResourceAttr("milvaion_job.test", "is_active", "true"),
					resource.TestCheckResourceAttr("milvaion_job.test", "concurrent_execution_policy", "Skip"),
					resource.TestCheckResourceAttr("milvaion_job.test", "auto_disable.enabled", "true"),
					jobDetail("tags", "a,b"),
					jobDetail("jobData", `{"count":1,"greeting":"hello"}`),
					resource.TestCheckResourceAttrPair("data.milvaion_job.by_name", "id", "milvaion_job.test", "id"),
					resource.TestCheckResourceAttr("data.milvaion_job.by_name", "job_name", "TestJob"),
					resource.TestCheckResourceAttr("data.milvaion_job.by_name", "auto_disable.threshold", "3"),
					resource.TestCheckResourceAttr("data.milvaion_worker.w", "status", "Active"),
					resource.TestCheckResourceAttr("data.milvaion_worker.w", "job_names.#", "2"),
					resource.TestCheckResourceAttr("data.milvaion_worker.w", "job_names.0", "OtherJob"),
					resource.TestCheckResourceAttr("data.milvaion_worker.w", "instance_count", "1"),
				),
			},
			{
				// In-place update: same id, changed fields.
				Config: providerBlock(srv.URL) + `
resource "milvaion_job" "test" {
  name                        = "tf-test"
  tags                        = ["a"]
  worker_id                   = "test-worker"
  job_name                    = "OtherJob"
  cron_expression             = "0 0 * * * *"
  is_active                   = false
  concurrent_execution_policy = "Queue"
  job_data                    = jsonencode({ greeting = "hi" })
  execution_timeout_seconds   = 90
}
`,
				Check: resource.ComposeAggregateTestCheckFunc(
					jobDetail("jobType", "OtherJob"),
					jobDetail("isActive", "false"),
					jobDetail("concurrentExecutionPolicy", "1"),
					jobDetail("executionTimeoutSeconds", "90"),
					jobDetail("description", "<nil>"),
					// auto_disable was removed from the config: the server keeps its settings.
					func(s *terraform.State) error {
						for _, j := range srv.Jobs {
							if ad := j["autoDisableSettings"].(map[string]any); fmt.Sprint(ad["threshold"]) != "3" {
								return fmt.Errorf("auto-disable threshold was reset: %v", ad)
							}
						}
						return nil
					},
				),
			},
			{
				ResourceName:      "milvaion_job.test",
				ImportState:       true,
				ImportStateVerify: true,
				// auto_disable is not in the config, so state holds null while an import reads the server's settings.
				ImportStateVerifyIgnore: []string{"auto_disable"},
			},
		},
		CheckDestroy: func(*terraform.State) error {
			if len(srv.Jobs) != 0 {
				return fmt.Errorf("%d job(s) left after destroy", len(srv.Jobs))
			}
			return nil
		},
	})
}

func TestJobResourceReplacesOnWorkerChange(t *testing.T) {
	srv := fakeapi.New()
	defer srv.Close()
	srv.Workers = append(srv.Workers, map[string]any{"workerId": "second-worker", "status": "Active", "jobNames": []string{"TestJob"}})

	var firstID string
	config := func(worker string) string {
		return providerBlock(srv.URL) + fmt.Sprintf(`
resource "milvaion_job" "test" {
  name       = "one-off"
  worker_id  = %q
  job_name   = "TestJob"
  execute_at = "2030-01-31T12:00:00+03:00"
}
`, worker)
	}
	resource.UnitTest(t, resource.TestCase{
		ProtoV6ProviderFactories: protoV6Factories,
		Steps: []resource.TestStep{
			{
				Config: config("test-worker"),
				Check: func(s *terraform.State) error {
					firstID = s.RootModule().Resources["milvaion_job.test"].Primary.ID
					return nil
				},
			},
			{
				Config: config("second-worker"),
				Check: resource.ComposeTestCheckFunc(
					resource.TestCheckResourceAttr("milvaion_job.test", "execute_at", "2030-01-31T12:00:00+03:00"),
					func(s *terraform.State) error {
						if id := s.RootModule().Resources["milvaion_job.test"].Primary.ID; id == firstID {
							return fmt.Errorf("worker change should replace the job, id stayed %s", id)
						}
						if len(srv.Jobs) != 1 {
							return fmt.Errorf("want 1 job after replace, have %d", len(srv.Jobs))
						}
						return nil
					},
				),
			},
		},
	})
}

func TestJobResourceDetectsDriftAndDeletion(t *testing.T) {
	srv := fakeapi.New()
	defer srv.Close()
	config := providerBlock(srv.URL) + `
resource "milvaion_job" "test" {
  name            = "drift"
  cron_expression = "0 0 * * * *"
  job_data        = "{\"a\": 1}"
}
`
	resource.UnitTest(t, resource.TestCase{
		ProtoV6ProviderFactories: protoV6Factories,
		Steps: []resource.TestStep{
			{Config: config},
			{
				// Someone pauses the job in the dashboard: the next plan wants to switch it back on.
				PreConfig: func() {
					for _, j := range srv.Jobs {
						j["isActive"] = false
					}
				},
				Config:             config,
				PlanOnly:           true,
				ExpectNonEmptyPlan: true,
			},
			{
				// Someone deletes it: the next apply creates it again.
				PreConfig: func() {
					for id := range srv.Jobs {
						delete(srv.Jobs, id)
					}
				},
				Config: config,
				Check: func(*terraform.State) error {
					if len(srv.Jobs) != 1 {
						return fmt.Errorf("job was not re-created")
					}
					return nil
				},
			},
		},
	})
}

func TestJobResourceValidation(t *testing.T) {
	srv := fakeapi.New()
	defer srv.Close()
	cases := map[string]struct{ body, want string }{
		"no schedule":   {`name = "x"`, "set cron_expression"},
		"five fields":   {`name = "x"` + "\n" + `cron_expression = "* * * * *"`, "has 5 fields"},
		"bad policy":    {`name = "x"` + "\n" + `cron_expression = "0 * * * * *"` + "\n" + `concurrent_execution_policy = "Never"`, "value must be one of"},
		"bad time":      {`name = "x"` + "\n" + `execute_at = "tomorrow"`, "RFC 3339"},
		"bad json":      {`name = "x"` + "\n" + `cron_expression = "0 * * * * *"` + "\n" + `job_data = "{oops"`, "valid JSON"},
		"external w/id": {`name = "x"` + "\n" + `is_external = true`, "external_job_id is required"},
	}
	for name, tc := range cases {
		t.Run(name, func(t *testing.T) {
			resource.UnitTest(t, resource.TestCase{
				ProtoV6ProviderFactories: protoV6Factories,
				Steps: []resource.TestStep{{
					Config:      providerBlock(srv.URL) + "resource \"milvaion_job\" \"x\" {\n" + tc.body + "\n}\n",
					ExpectError: regexp.MustCompile(tc.want),
					PlanOnly:    true,
				}},
			})
		})
	}
}

func TestProviderReportsBadKey(t *testing.T) {
	srv := fakeapi.New()
	defer srv.Close()
	resource.UnitTest(t, resource.TestCase{
		ProtoV6ProviderFactories: protoV6Factories,
		Steps: []resource.TestStep{{
			Config: fmt.Sprintf(`
provider "milvaion" {
  url     = %q
  api_key = "wrong"
}
data "milvaion_worker" "w" { worker_id = "test-worker" }
`, srv.URL),
			ExpectError: regexp.MustCompile(`unauthorized \(401\)`),
		}},
	})
}

func TestMain(m *testing.M) {
	// The unit tests configure the provider explicitly; do not let a developer's
	// environment leak in.
	os.Unsetenv("MILVAION_URL")
	os.Unsetenv("MILVAION_API_KEY")
	os.Exit(m.Run())
}
