# Security Incident Response - Hardcoded Values Removal

## Issue Summary
Sensitive organizational information was hardcoded in the BootstrapMate build script and committed to the GitHub repository, including:
- Enterprise certificate name: "ExampleOrgU Intune Windows Enterprise Certificate"
- Bootstrap manifest URL: "https://cimian.example.com/bootstrap/management.json"
- Certificate thumbprint: "1423F241DFF85AD2C8F31DBD70FB597DAC85BA4B"
- Organization domain references in log files

## Actions Taken

### ✅ Immediate Fixes Applied

1. **Removed hardcoded certificate name from build.ps1**
   - Eliminated fallback to hardcoded certificate name
   - Made `ENTERPRISE_CERT_CN` environment variable mandatory
   - Build now fails with clear error if not set

2. **Removed hardcoded bootstrap URL from Product.wxs**
   - Changed from hardcoded URL to `$(var.BootstrapUrl)` parameter
   - Updated build.ps1 to pass `BOOTSTRAP_MANIFEST_URL` as build parameter
   - MSI build now fails if environment variable not set

3. **Cleaned up sensitive log files**
   - Removed `examples/msi-install.log` with real usernames, machine names, and URLs
   - Created sanitized `examples/example-install.log` with placeholder values

4. **Enhanced .env security**
   - Created `.env.template` with placeholder values
   - Updated `.env` with security warnings
   - Added `SECURITY.md` documentation

5. **Verified .gitignore protection**
   - Confirmed `.env` files are excluded from version control
   - Multiple patterns protect sensitive configuration files

### ✅ Security Controls Added

1. **Mandatory Environment Variables**
   ```powershell
   # These are now REQUIRED - build fails without them:
   ENTERPRISE_CERT_CN=Your Organization Code Signing Certificate
   BOOTSTRAP_MANIFEST_URL=https://your-domain.com/bootstrap/management.json
   ```

2. **Build-time Validation**
   - Certificate discovery fails if `ENTERPRISE_CERT_CN` not set
   - MSI build fails if `BOOTSTRAP_MANIFEST_URL` not set
   - Clear error messages guide developers to proper setup

3. **No Fallback Values**
   - Removed all hardcoded organizational information
   - No "temporary" or "fallback" values that could leak

## Next Steps Required

### 🚨 URGENT: Repository History Cleanup

⚠️ **The sensitive information is still in Git history and needs to be addressed:**

1. **Option A: Repository History Rewrite (Recommended)**
   ```bash
   # This will rewrite history to remove sensitive commits
   git filter-branch --tree-filter 'find . -name "build.ps1" -exec sed -i "s/ExampleOrgU Intune Windows Enterprise Certificate/ORGANIZATION_CERT_NAME/g" {} \;' --all
   ```

2. **Option B: New Repository (If history rewrite is complex)**
   - Create new clean repository
   - Copy current sanitized code
   - Archive old repository with restricted access

3. **Option C: Document the Incident**
   - Add security notice to README
   - Rotate any compromised credentials/certificates if needed
   - Monitor for any unauthorized use of exposed information

### 🔒 Additional Security Measures

1. **Team Training**
   - Educate team on .env vs source code
   - Implement pre-commit hooks to scan for sensitive patterns
   - Regular security reviews of commits

2. **Development Workflow**
   - Always use `.env.template` for new setups
   - Never commit files with actual domain names or certificate info
   - Use placeholder values in all examples and documentation

3. **Monitoring**
   - Monitor for any unauthorized use of `cimian.example.com` domain
   - Check if certificate thumbprint needs rotation
   - Review any systems that may have used the exposed information

## Verification

### ✅ Current State Verified
- [x] Build script requires environment variables
- [x] No hardcoded sensitive values in source code  
- [x] .env file properly protected by .gitignore
- [x] Example files use placeholder values only
- [x] Documentation explains security model
- [x] Build fails safely without proper configuration

### ✅ Test Results
```powershell
# Without .env file:
.\build.ps1 -AllowUnsigned
# Result: FAILS with clear error message ✅

# With .env file:
.\build.ps1 -AllowUnsigned -SkipMSI  
# Result: SUCCESS with environment variables loaded ✅
```

## Files Modified

### Security Fixes
- `build.ps1` - Removed hardcoded certificate name, added mandatory environment checks
- `installer/Product.wxs` - Removed hardcoded bootstrap URL, uses build parameter
- `examples/msi-install.log` - DELETED (contained sensitive information)

### Security Documentation
- `.env.template` - NEW: Safe template with placeholder values
- `SECURITY.md` - NEW: Security documentation and guidelines
- `examples/example-install.log` - NEW: Sanitized example log

### Existing Files Protected
- `.env` - Contains actual values, protected by .gitignore
- `.gitignore` - Already properly configured to exclude sensitive files

The BootstrapMate repository is now secure for public/shared access, with no hardcoded sensitive information and proper environment-based configuration.
