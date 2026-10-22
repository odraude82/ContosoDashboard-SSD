using System.ComponentModel.DataAnnotations;
using ContosoDashboard.Options;
using Xunit;

namespace ContosoDashboard.Tests.Unit;

public sealed class DocumentOptionsTests
{
    [Fact]
    public void StorageRootMustBeOutsideWebRoot()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "contoso-options");
        var webRoot = Path.Combine(contentRoot, "wwwroot");

        Assert.False(DocumentStorageOptions.IsStorageRootOutsideWebRoot("wwwroot/uploads", contentRoot, webRoot));
        Assert.True(DocumentStorageOptions.IsStorageRootOutsideWebRoot("AppData/uploads", contentRoot, webRoot));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("example.com", false)]
    [InlineData("192.0.2.10", false)]
    public void ScannerHostMustBeLoopback(string host, bool expectedValid)
    {
        var options = new MalwareScannerOptions { Host = host };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.Equal(expectedValid, isValid);
    }
}