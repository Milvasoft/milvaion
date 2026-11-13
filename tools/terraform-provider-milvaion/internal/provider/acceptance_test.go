package provider

import (
	"context"
	"fmt"
	"os"
	"testing"
	"time"

	"github.com/hashicorp/terraform-plugin-testing/helper/resource"
	"github.com/hashicorp/terraform-plugin-testing/terraform"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
)

// checkDestroyed verifies that every milvaion_job in the state is gone from the server.
func checkDestroyed(url, key string) resource.TestCheckFunc {
	return func(s *terraform.State) error {
		c := client.New(url, key)
		for _, rs := range s.RootModule().Resources {
			if rs.Type != "milvaion_job" {
				continue
			}
			if _, err := c.GetJob(context.Background(), rs.Primary.ID); !client.IsNotFound(err) {
				return fmt.Errorf("job %s still exists after destroy (err=%v)", rs.Primary.ID, err)
			}
		}
		return nil
	}
}

// Acceptance tests run against a real Milvaion. They are skipped unless TF_ACC
// is set, and need:
//
//	MILVAION_ACC_URL             e.g. http://localhost:5000
//	MILVAION_ACC_API_KEY         a key with ScheduledJobManagement.* and WorkerManagement.List/Detail
//	MILVAION_ACC_WORKER_ID       an active worker, e.g. sample-worker from docker-compose
//	MILVAION_ACC_JOB_NAME        a job type that worker offers, e.g. TestJob
//
// They create and delete jobs whose names start with "tf-acc-".

func accEnv(t *testing.T) (url, key, worker, jobName string) {
	t.Helper()
	if os.Getenv("TF_ACC") == "" {
		t.Skip("set TF_ACC=1 to run acceptance tests against a real Milvaion")
	}
	url, key = os.Getenv("MILVAION_ACC_URL"), os.Getenv("MILVAION_ACC_API_KEY")
	worker, jobName = os.Getenv("MILVAION_ACC_WORKER_ID"), os.Getenv("MILVAION_ACC_JOB_NAME")
	if url == "" || key == "" || worker == "" || jobName == "" {
		t.Fatal("acceptance tests need MILVAION_ACC_URL, MILVAION_ACC_API_KEY, MILVAION_ACC_WORKER_ID and MILVAION_ACC_JOB_NAME")
	}
	return
}

func TestAccJob(t *testing.T) {
	url, key, worker, jobName := accEnv(t)
	name := fmt.Sprintf("tf-acc-%d", time.Now().UnixNano())
	provider := fmt.Sprintf("provider \"milvaion\" {\n  url = %q\n  api_key = %q\n}\n", url, key)

	resource.Test(t, resource.TestCase{
		ProtoV6ProviderFactories: protoV6Factories,
		Steps: []resource.TestStep{
			{
				Config: provider + fmt.Sprintf(`
data "milvaion_worker" "w" {
  worker_id = %[2]q
}

resource "milvaion_job" "test" {
  name            = %[1]q
  description     = "created by the acceptance tests"
  tags            = ["tf-acc"]
  worker_id       = data.milvaion_worker.w.worker_id
  job_name        = %[3]q
  cron_expression = "0 0 3 * * *"
  is_active       = false
  job_data        = jsonencode({ source = "terraform" })
  auto_disable = {
    threshold              = 4
    failure_window_minutes = 30
  }
}

data "milvaion_job" "lookup" {
  name       = milvaion_job.test.name
  depends_on = [milvaion_job.test]
}
`, name, worker, jobName),
				Check: resource.ComposeAggregateTestCheckFunc(
					resource.TestCheckResourceAttrSet("milvaion_job.test", "id"),
					resource.TestCheckResourceAttr("data.milvaion_worker.w", "status", "Active"),
					resource.TestCheckTypeSetElemAttr("data.milvaion_worker.w", "job_names.*", jobName),
					resource.TestCheckResourceAttrPair("data.milvaion_job.lookup", "id", "milvaion_job.test", "id"),
					resource.TestCheckResourceAttr("data.milvaion_job.lookup", "auto_disable.threshold", "4"),
					resource.TestCheckResourceAttr("data.milvaion_job.lookup", "job_data", `{"source":"terraform"}`),
				),
			},
			{
				Config: provider + fmt.Sprintf(`
resource "milvaion_job" "test" {
  name                        = %[1]q
  tags                        = ["tf-acc", "updated"]
  worker_id                   = %[2]q
  job_name                    = %[3]q
  cron_expression             = "0 30 3 * * *"
  is_active                   = false
  concurrent_execution_policy = "Queue"
  execution_timeout_seconds   = 120
  job_data                    = jsonencode({ source = "terraform", step = 2 })
}
`, name, worker, jobName),
				Check: resource.ComposeAggregateTestCheckFunc(
					resource.TestCheckResourceAttr("milvaion_job.test", "concurrent_execution_policy", "Queue"),
					resource.TestCheckResourceAttr("milvaion_job.test", "tags.#", "2"),
				),
			},
			{
				ResourceName:            "milvaion_job.test",
				ImportState:             true,
				ImportStateVerify:       true,
				ImportStateVerifyIgnore: []string{"auto_disable"},
			},
		},
		CheckDestroy: checkDestroyed(url, key),
	})
}

func TestAccOneTimeJob(t *testing.T) {
	url, key, worker, jobName := accEnv(t)
	name := fmt.Sprintf("tf-acc-once-%d", time.Now().UnixNano())
	at := time.Now().Add(240 * time.Hour).UTC().Truncate(time.Second).Format(time.RFC3339)
	resource.Test(t, resource.TestCase{
		ProtoV6ProviderFactories: protoV6Factories,
		Steps: []resource.TestStep{
			{
				Config: fmt.Sprintf(`
provider "milvaion" {
  url     = %q
  api_key = %q
}
resource "milvaion_job" "once" {
  name       = %q
  worker_id  = %q
  job_name   = %q
  execute_at = %q
  is_active  = false
}
`, url, key, name, worker, jobName, at),
				Check: resource.TestCheckResourceAttr("milvaion_job.once", "execute_at", at),
			},
			{
				ResourceName:            "milvaion_job.once",
				ImportState:             true,
				ImportStateVerify:       true,
				ImportStateVerifyIgnore: []string{"auto_disable"},
			},
		},
		CheckDestroy: checkDestroyed(url, key),
	})
}
