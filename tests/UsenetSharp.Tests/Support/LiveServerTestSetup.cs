namespace UsenetSharpTest.Clients
{
    [SetUpFixture]
    [Category("Integration")]
    public sealed class LiveClientTestSetup
    {
        [OneTimeSetUp]
        public void RequireCredentials()
        {
            if (!Credentials.AreConfigured)
            {
                Assert.Ignore(
                    "Set USENETSHARP_TEST_HOST, USENETSHARP_TEST_USERNAME, and USENETSHARP_TEST_PASSWORD.");
            }
        }
    }
}

namespace UsenetSharpTest.Integration
{
    [SetUpFixture]
    [Category("Integration")]
    public sealed class LiveIntegrationTestSetup
    {
        [OneTimeSetUp]
        public void RequireCredentials()
        {
            if (!Credentials.AreConfigured)
            {
                Assert.Ignore(
                    "Set USENETSHARP_TEST_HOST, USENETSHARP_TEST_USERNAME, and USENETSHARP_TEST_PASSWORD.");
            }
        }
    }
}
