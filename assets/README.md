# BootstrapMate Assets

This directory contains assets used by the MSI installer:

- `bootstrapmate.ico` - Application icon for Add/Remove Programs and shortcuts

## Icon Requirements

The icon file should be a standard Windows ICO file with multiple sizes:
- 16x16 pixels
- 32x32 pixels  
- 48x48 pixels
- 256x256 pixels

## Creating the Icon

If you don't have an existing icon, you can:

1. Use any online ICO converter to create one from a PNG/JPG
2. Use the default Windows application icon template
3. Create a simple branded icon for your organization

For now, the MSI will work without the icon file (it will just use the default installer icon).
