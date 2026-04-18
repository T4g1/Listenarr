using Microsoft.EntityFrameworkCore;
using Moq;

namespace Listenarr.Api.Tests
{
    public class BaseTests : IDisposable
    {
        private string? _tempFolder = null;

        /// <summary>
        /// SETUP: Runs before EVERY test
        /// </summary>
        public BaseTests()
        {
            _tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(_tempFolder);
        }

        public string GetTempPath()
        {
            if (_tempFolder == null)
            {
                _tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(_tempFolder);
            }
            
            return _tempFolder;
        }

        public string GetTempDirectory(string directory)
        {
            var path = Path.Join(GetTempPath(), directory);
            Directory.CreateDirectory(path);

            return path;
        }

        public async Task<string> GetFileAsync(string directory, string filename)
        {
            var path = Path.Join(directory, filename);
            await File.WriteAllTextAsync(path, "test");

            return path;
        }
        
        /// <summary>
        /// Returns an empty database
        /// </summary>
        public static IDbContextFactory<ListenArrDbContext> CreateDB()
        {
            return CreateDB(db => {});
        }

        /// <summary>
        /// Returns a database initialized using the provided method
        /// </summary>
        public static IDbContextFactory<ListenArrDbContext> CreateDB(Action<ListenArrDbContext> initDb)
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            
            using var context = new ListenArrDbContext(options);
            initDb(context);
            context.SaveChanges();

            var mockFactory = new Mock<IDbContextFactory<ListenArrDbContext>>();
            
            mockFactory
                .Setup(factory => factory.CreateDbContext())
                .Returns(() => new ListenArrDbContext(options));
            
            return mockFactory.Object;
        }

        /// <summary>
        /// CLEANUP: Runs after EVERY test
        /// </summary>
        public void Dispose()
        {
            if (_tempFolder != null && Directory.Exists(_tempFolder))
            {
                Directory.Delete(_tempFolder, true);
            }
        }
    }
}
