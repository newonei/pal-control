# Process-scoped defaults for integration tests that start the Control API.
# Economy-specific smokes override ExtractionMode on their own command line.
$env:PlayerPortal__Enabled = "false"
$env:ExtractionMode__Enabled = "false"

# This fixed principal is accepted only because tracked development settings
# explicitly enable DevelopmentMode. It is test data, never production config.
$env:Security__AdminAuthentication__Enabled = "true"
$env:Security__AdminAuthentication__EnableLoopbackDevelopmentPrincipal = "true"
$env:Security__AdminAuthentication__DevelopmentPrincipalSubject = "integration-test"
$env:Security__AdminAuthentication__Principals__0__Subject = "integration-test"
$env:Security__AdminAuthentication__Principals__0__ApiKeySha256 =
    "87052f5138109134ec8e8b25a5e18545e39c90244679e52b6c40c364cb671060"
$env:Security__AdminAuthentication__Principals__0__Roles__0 = "Owner"
$env:Security__AdminAuthentication__Principals__0__TotpSecretBase32 =
    "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"
