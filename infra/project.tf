import {
  to = render_project.orbit
  id = "prj-d6tc2ii4d50c73c7gb9g"
}

resource "render_project" "orbit" {
  name = "Orbit"

  environments = {
    Production = {
      name             = "Production"
      protected_status = "unprotected"
    }
    Staging = {
      name             = "Staging"
      protected_status = "unprotected"
    }
  }
}
