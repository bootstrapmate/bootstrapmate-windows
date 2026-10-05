using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BootstrapMate.Core;

/// <summary>
/// Keeps ProgramData\ManagedBootstrap for administrators. BootstrapMate runs as SYSTEM and
/// acts on what it finds there (the force file, cached installers, its own records), so
/// nothing in it may come from a standard user.
/// </summary>
/// <remarks>
/// ProgramData's default ACL lets any user create files and folders below it, and a
/// folder created there inherits that. Before a run reads anything, this resets the
/// folder to SYSTEM and Administrators full control and Users read, not inherited, and
/// removes every entry a non-administrator owns and every reparse point. The MSI sets
/// the same ACL when it installs.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class DataDirectoryGuard
{
    public static readonly string DefaultRoot = @"C:\ProgramData\ManagedBootstrap";

    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>
    /// The owner this process may assign: SYSTEM when it runs as SYSTEM; an elevated
    /// administrator may only assign the Administrators group.
    /// </summary>
    private static SecurityIdentifier AssignableOwner()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User == System ? System : Administrators;
    }

    /// <summary>Owners whose files a SYSTEM run may trust.</summary>
    public static bool IsTrustedOwner(SecurityIdentifier? owner) =>
        owner is not null && (owner == System || owner == Administrators || owner == TrustedInstaller);

    /// <summary>
    /// Locks <paramref name="root"/> down and returns one line per thing it removed or could
    /// not fix, for the caller to log once its log is open. Must run elevated.
    /// </summary>
    public static List<string> Secure(string? root = null)
    {
        var target = root ?? DefaultRoot;
        var notes = new List<string>();

        try
        {
            var info = new DirectoryInfo(target);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // A link in place of the folder would send every write somewhere else.
                Directory.Delete(target);
                notes.Add($"Removed {target}: it was a link, not a folder");
            }
            // Owner SYSTEM (Administrators from an elevated session) as well: whoever owns
            // the folder can rewrite its ACL.
            Directory.CreateDirectory(target);
            var folder = new DirectoryInfo(target);
            folder.SetAccessControl(LockedSecurity(withOwner: true, notes));
            Sweep(folder, notes);
        }
        catch (Exception ex)
        {
            notes.Add($"Could not secure {target}: {ex.Message}");
        }

        return notes;
    }

    /// <summary>
    /// SYSTEM and Administrators full control, Users read, inherited by everything below and
    /// not inheriting from ProgramData. Same as the MSI's SDDL.
    /// </summary>
    private static DirectorySecurity LockedSecurity(bool withOwner, List<string> notes)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        if (withOwner)
        {
            try { security.SetOwner(AssignableOwner()); }
            catch (Exception ex) { notes.Add($"Owner not changed: {ex.Message}"); }
        }
        return security;
    }

    /// <summary>
    /// Removes every entry a non-administrator owns, and every reparse point, below
    /// <paramref name="directory"/>. Links are deleted, never followed.
    /// </summary>
    private static void Sweep(DirectoryInfo directory, List<string> notes)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = directory.EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                RecurseSubdirectories = false
            }).ToList();
        }
        catch (Exception ex)
        {
            notes.Add($"Could not list {directory.FullName}: {ex.Message}");
            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                bool link = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                var owner = OwnerOf(entry);
                if (!link && !IsTrustedOwner(owner) && UnderLogs(entry))
                {
                    // Logs are only read, never acted on, and an elevated run by an Entra ID
                    // administrator owns what it writes under its own SID. Keep them; just
                    // take them back for SYSTEM.
                    try
                    {
                        Reclaim(entry);
                        notes.Add($"Took ownership of {entry.FullName} (was {owner?.Value ?? "unknown owner"})");
                        if (entry is DirectoryInfo logDir) Sweep(logDir, notes);
                    }
                    catch (Exception ex)
                    {
                        // Its own ACL shuts SYSTEM out; the folder above still lets it go.
                        if (entry is DirectoryInfo d) Directory.Delete(d.FullName, recursive: true);
                        else File.Delete(entry.FullName);
                        notes.Add($"Removed {entry.FullName}: could not take ownership ({ex.Message})");
                    }
                }
                else if (link || !IsTrustedOwner(owner))
                {
                    var why = link ? "a link" : $"created by a non-administrator ({owner?.Value ?? "unknown owner"})";
                    if (entry is DirectoryInfo dir)
                        // A link is removed on its own; a real folder with everything in it.
                        Directory.Delete(dir.FullName, recursive: !link);
                    else
                        File.Delete(entry.FullName);
                    notes.Add($"Removed {entry.FullName}: {why}");
                }
                else if (entry is DirectoryInfo dir)
                {
                    Sweep(dir, notes);
                }
            }
            catch (Exception ex)
            {
                notes.Add($"Could not check {entry.FullName}: {ex.Message}");
            }
        }
    }

    private static bool UnderLogs(FileSystemInfo entry) =>
        entry.FullName.Contains(@"\logs\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Owner SYSTEM (or Administrators), explicit entries dropped, the folder's ACL inherited again.</summary>
    private static void Reclaim(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo dir)
        {
            var security = new DirectorySecurity();
            security.SetOwner(AssignableOwner());
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            dir.SetAccessControl(security);
        }
        else if (entry is FileInfo file)
        {
            var security = new FileSecurity();
            security.SetOwner(AssignableOwner());
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            file.SetAccessControl(security);
        }
    }

    private static SecurityIdentifier? OwnerOf(FileSystemInfo entry)
    {
        try
        {
            return entry switch
            {
                DirectoryInfo d => d.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier,
                FileInfo f => f.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
