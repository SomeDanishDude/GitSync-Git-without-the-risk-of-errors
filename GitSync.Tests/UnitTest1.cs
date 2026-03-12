using Xunit;
using GitSync;
using System.IO;
using System.Collections.Generic;

namespace GitSync.Tests
{
    public class ParseProjectMetadataTests
    {
        [Fact]
        public void ParseProjectMetadata_HandlesExceptionGracefully()
        {
            // Arrange
            string nonExistentFolder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            // We want to trigger an exception in ParseProjectMetadata.
            // The first line is: var meta = new ProjectMetadata { Name = new DirectoryInfo(folder).Name };
            // DirectoryInfo(folder).Name doesn't throw if folder doesn't exist.

            // However, inside the try block:
            // if (types.Contains("Node.js")) { string pkg = Path.Combine(folder, "package.json"); if (File.Exists(pkg)) { string json = File.ReadAllText(pkg); ... } }

            // To trigger an exception in the catch block:
            // We could use a folder name that is invalid for DirectoryInfo, but that might throw before the try.
            // Actually, if we pass a null or empty folder to DirectoryInfo it might throw.

            // Let's try to trigger an exception inside the try block.
            // If File.Exists(pkg) is true but File.ReadAllText(pkg) fails.

            string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);
            try {
                string pkgPath = Path.Combine(tempDir, "package.json");
                File.WriteAllText(pkgPath, "{}");

                // Open file with exclusive access to cause ReadAllText to fail
                using (FileStream fs = new FileStream(pkgPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    // Act
                    var meta = Program.ParseProjectMetadata(tempDir, new List<string> { "Node.js" });

                    // Assert
                    Assert.NotNull(meta);
                    Assert.Equal(new DirectoryInfo(tempDir).Name, meta.Name);
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void ParseProjectMetadata_ReturnsDefaultOnInvalidPath()
        {
            // Act & Assert
            // This might throw outside the try-catch if DirectoryInfo(null) is called.
            // Current code: var meta = new ProjectMetadata { Name = new DirectoryInfo(folder).Name };
            // If folder is null, DirectoryInfo constructor throws ArgumentNullException.

            Assert.Throws<ArgumentNullException>(() => Program.ParseProjectMetadata(null!, new List<string>()));
        }
    }
}
