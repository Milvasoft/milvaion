terraform {
  required_providers {
    milvaion = {
      source = "milvasoft/milvaion"
    }
  }
}

# url and api_key fall back to MILVAION_URL and MILVAION_API_KEY.
provider "milvaion" {
  url = "https://milvaion.example.com"
}
