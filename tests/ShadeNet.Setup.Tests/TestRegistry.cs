namespace ShadeNet.Setup.Tests;

/// <summary>
/// The test classes that write under <c>HKCU\Software\ShadeNetTests</c> run one after the other: each removes that parent key
/// when it is left empty, and doing so while another class creates its key under it fails ("a registry key that has been
/// marked for deletion").
/// </summary>
[CollectionDefinition(Collection)]
public sealed class TestRegistry
{
    public const string Collection = "HKCU ShadeNetTests";
}
