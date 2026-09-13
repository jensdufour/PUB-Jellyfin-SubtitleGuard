# Changelog

## 0.1.1

- First packaged public release for Jellyfin 12.0 and .NET 10.
- Preserve the existing plugin GUID and assembly name.
- Provide a DLL-only archive, MD5 catalog checksum, and separate ZIP/DLL SHA-256 checksums.
- Guard subtitle-cache publication with complete-input, successful-extraction and expected-output checks; retain all supplied languages.
- Require explicit source policy and coordinated updates to any external startup gate. Packaging does not change installed servers or the prior 0.1.0 deployment.