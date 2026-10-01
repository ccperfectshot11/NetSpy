// NetSpy: static replacement for the file upstream generates from DecompilerVersionInfo.template.cs
// (vendored from NetSpy master aa9e3de, 2026-09-27).
public static class DecompilerVersionInfo
{
	public const string Major = "11";
	public const string Minor = "1";
	public const string Build = "0";
	public const string Revision = "0";
	public const string VersionName = null;

	public const string Version = Major + "." + Minor + "." + Build + "." + Revision;
	public const string FullVersion = Major + "." + Minor + "." + Build + ".0-netspy";
	public const string FullVersionWithShortCommitHash = FullVersion + "+aa9e3de";
	public const string FullVersionWithCommitHash = FullVersion + "+aa9e3dedb393f965d45395d3cc323658560dc3ba";
}
