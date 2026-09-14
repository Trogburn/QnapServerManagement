# Photo Manager — install and first use

This zip is the Windows app. You do not need GitHub, Visual Studio, or the
.NET SDK. **macOS is not supported.**

You need:

- Windows 10 or 11, 64-bit
- Read access to your photo share
- Write access to a **separate** quarantine folder
- PowerShell (already on Windows)

## Install

1. Extract the zip to a folder.
2. Double-click `Install.bat`.
3. If Windows SmartScreen asks, choose **More info**, then **Run anyway**. The
   build is not code-signed.
4. Open **Photo Manager** from the Start Menu.

No administrator rights are required. Files go to
`%LOCALAPPDATA%\PhotoManager`.

You can skip install and double-click `PhotoManager.exe` in the extracted
folder instead.

## First use

The boxes start with placeholders such as `\\SERVER\Share\Photos`. Replace
those with **your** folders.

1. **Scan root:** the photos you want to review. Use a UNC path such as
   `\\NAS\Share\Photos`, or a folder on a local drive such as `D:\Photos`.
   Do not use a drive root (`C:\`) or a mapped network letter (`P:\`).
   For a NAS, prefer the UNC path.
2. **Quarantine root:** a folder **outside** that photo tree. Duplicates that
   you approve for removal are moved here, not deleted.
3. **Artifact root:** leave `artifacts`. That evidence stays on this PC, not
   inside the photo share.
4. On the NAS, take a snapshot (or other backup) **before** you Apply anything.
5. Choose one workflow from Configuration:
   - **Start rotate work** — review EXIF orientation and content-based
     proposals, then bake approved rotations into the pixels so the photo is
     upright in every viewer. Use the preview rotate buttons when a photo is
     still sideways.
   - **Configure and continue to duplicate work** — find duplicate and similar
     photos, review, then quarantine extras.
   - **Start date work** — review timestamp repairs, then apply only approved
     files.
6. Scan and review first. **Apply** is a separate step. Start with a small
   batch and confirm the result before doing more.

Nothing moves, deletes, or changes timestamps until you click Apply. Keep
suggestions in the reviewer are not approvals.

## After a scan

- Rotate work: review photos that EXIF says are sideways or upside down,
  then Apply. Apply re-encodes JPEG and keeps a backup for Undo.
- Duplicate work: review groups, then Dry-run, then Apply to quarantine.
- Date work: review proposed dates, then Apply. Conflicts stay until you
  choose a date.
- Use **Back to configuration** to switch workflows. **Reset entire session**
  only clears this session in memory; it does not undo files already Applied.

If something looks wrong after Apply, use the app's Undo on that session.
Do not move files around in Explorer to "fix" a quarantine.

## Upgrade

Close Photo Manager, then run `Install.bat` from the new zip. That
replaces the copy under `%LOCALAPPDATA%\PhotoManager`.

## Optional thanks

The app is free. If it helped, you can send coffee money via
[GitHub Sponsors](https://github.com/sponsors/Trogburn). You do not need to
do that to keep using it.
