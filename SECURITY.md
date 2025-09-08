# Security Notice

## Environment Variables Required

BootstrapMate requires environment variables to be set for security and configuration. **Never hardcode sensitive information in source code.**

### Required Setup

1. **Copy the template file:**
   ```powershell
   Copy-Item .env.template .env
   ```

2. **Edit .env with your organization's values:**
   ```properties
   ENTERPRISE_CERT_CN=Your Organization Code Signing Certificate
   BOOTSTRAP_MANIFEST_URL=https://your-domain.com/bootstrap/management.json
   ```

3. **The .env file is excluded from version control** - it contains sensitive information specific to your deployment.

## What's Protected

- **Enterprise certificate names and thumbprints**
- **Bootstrap manifest URLs**
- **Organization-specific domain names**
- **Authentication credentials**

## Build Requirements

The build script will fail if required environment variables are not set:

- `ENTERPRISE_CERT_CN` - Required for certificate discovery
- `BOOTSTRAP_MANIFEST_URL` - Required for MSI build

This ensures no builds can succeed without proper configuration, preventing accidental deployment of development or example values.

## Files to Never Commit

- `.env` - Contains actual organization values
- `*.log` files from real deployments
- Files with actual domain names, certificate info, or usernames
- Any files containing `cimian.example.com`, `ExampleOrgU`, or specific certificate thumbprints

## Safe Examples

The `examples/` directory contains sanitized examples with placeholder values:
- `bootstrapmate.json` - Example manifest with `your-domain.com`
- `example-install.log` - Sanitized log output
- Detection scripts with generic values

Always use the template files and examples as starting points for your deployment.
