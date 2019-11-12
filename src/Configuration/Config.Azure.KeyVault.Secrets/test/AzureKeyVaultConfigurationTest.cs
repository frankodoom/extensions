// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Testing;
using Microsoft.Extensions.Configuration.Test;
using Microsoft.Extensions.Primitives;
using Moq;
using Xunit;
using Action = System.Action;

namespace Microsoft.Extensions.Configuration.AzureKeyVault.Test
{
    public class AzureKeyVaultConfigurationTest: ConfigurationProviderTestBase
    {
        private static readonly TimeSpan NoReloadDelay = TimeSpan.FromMilliseconds(1);

        private void SetPages(Mock<SecretClient> mock, params KeyVaultSecret[][] pages)
        {
            SetPages(mock, pages);
        }

        private void SetPages(Mock<SecretClient> mock, Func<string, Task> getSecretCallback, params KeyVaultSecret[][] pages)
        {
            getSecretCallback ??= (_ => Task.CompletedTask);

            var pagesOfProperties = pages.Select(
                page => page.Select(secret => secret.Properties).ToArray()).ToArray();

            mock.Setup(m => m.GetPropertiesOfSecretsAsync(default)).Returns(new MockAsyncPageable(pagesOfProperties));

            foreach (var page in pages)
            {
                foreach (var secret in page)
                {
                    mock.Setup(client => client.GetSecretAsync(secret.Name, null, default))
                        .Callback((string name, string label, CancellationToken token) => getSecretCallback(name))
                        .ReturnsAsync(Response.FromValue(secret, Mock.Of<Response>()));
                }   
            }
        }

        private class MockAsyncPageable: AsyncPageable<SecretProperties>
        {
            private readonly SecretProperties[][] _pages;

            public MockAsyncPageable(SecretProperties[][] pages)
            {
                _pages = pages;
            }

            public override async IAsyncEnumerable<Page<SecretProperties>> AsPages(string continuationToken = null, int? pageSizeHint = null)
            {
                foreach (var page in _pages)
                {
                    yield return Page<SecretProperties>.FromValues(page, null, Mock.Of<Response>());
                }

                await Task.CompletedTask;
            }
        }
        [Fact]
        public void LoadsAllSecretsFromVault()
        {
            var client = new Mock<SecretClient>();
            SetPages(client, 
                new []
                {
                    CreateSecret("Secret1", "Value1")
                },
                new []
                {
                    CreateSecret("Secret2", "Value2")
                }
                );

            // Act
            using (var provider = new AzureKeyVaultConfigurationProvider(client.Object,  new DefaultKeyVaultSecretManager()))
            {
                provider.Load();

                var childKeys = provider.GetChildKeys(Enumerable.Empty<string>(), null).ToArray();
                Assert.Equal(new[] { "Secret1", "Secret2" }, childKeys);
                Assert.Equal("Value1", provider.Get("Secret1"));
                Assert.Equal("Value2", provider.Get("Secret2"));
            }
        }

        private KeyVaultSecret CreateSecret(string name, string value, bool? enabled = true, DateTimeOffset? updated = null)
        {
            var id = new Uri("http://azure.keyvault/" + name);

            var secretProperties = SecretModelFactory.SecretProperties(id, name:name, updatedOn: updated);
            secretProperties.Enabled = enabled;

            return SecretModelFactory.KeyVaultSecret(secretProperties, value);
        }

        [Fact]
        public void DoesNotLoadFilteredItems()
        {
            var client = new Mock<SecretClient>();
            SetPages(client,
                new []
                {
                    CreateSecret("Secret1", "Value1")
                },
                new []
                {
                    CreateSecret("Secret2", "Value2")
                }
            );

            // Act
            using (var provider = new AzureKeyVaultConfigurationProvider(client.Object, new EndsWithOneKeyVaultSecretManager()))
            {
                provider.Load();

                // Assert
                var childKeys = provider.GetChildKeys(Enumerable.Empty<string>(), null).ToArray();
                Assert.Equal(new[] { "Secret1" }, childKeys);
                Assert.Equal("Value1", provider.Get("Secret1"));
            }
        }

        [Fact]
        public void DoesNotLoadDisabledItems()
        {
            var client = new Mock<SecretClient>();
            SetPages(client,
                new []
                {
                    CreateSecret("Secret1", "Value1")
                },
                new []
                {
                    CreateSecret("Secret2", "Value2", enabled: false),
                    CreateSecret("Secret3", "Value3", enabled: null),
                }
            );

            // Act
            using (var provider = new AzureKeyVaultConfigurationProvider(client.Object, new DefaultKeyVaultSecretManager()))
            {
                provider.Load();

                // Assert
                var childKeys = provider.GetChildKeys(Enumerable.Empty<string>(), null).ToArray();
                Assert.Equal(new[] { "Secret1" }, childKeys);
                Assert.Equal("Value1", provider.Get("Secret1"));
                Assert.Throws<InvalidOperationException>(() => provider.Get("Secret2"));
                Assert.Throws<InvalidOperationException>(() => provider.Get("Secret3"));
            }
        }
        
        [Fact]
        public void SupportsReload()
        {
            var updated = DateTime.Now;

            var client = new Mock<SecretClient>();
            SetPages(client,
                new []
                {
                    CreateSecret("Secret1", "Value1", enabled: true, updated: updated)
                }
            );

            // Act & Assert
            using (var provider = new AzureKeyVaultConfigurationProvider(client.Object, new DefaultKeyVaultSecretManager()))
            {
                provider.Load();

                Assert.Equal("Value1", provider.Get("Secret1"));

                SetPages(client,
                    new []
                    {
                        CreateSecret("Secret1", "Value2", enabled: true, updated: updated.AddSeconds(1))
                    }
                );

                provider.Load();
                Assert.Equal("Value2", provider.Get("Secret1"));
            }
        }
        
        [Fact]
        public async Task SupportsAutoReload()
        {
            var updated = DateTime.Now;
            int numOfTokensFired = 0;

            var client = new Mock<SecretClient>();
            SetPages(client,
                new []
                {
                    CreateSecret("Secret1", "Value1", enabled: true, updated: updated)
                }
            );

            // Act & Assert
            using (var provider = new ReloadControlKeyVaultProvider(client.Object, new DefaultKeyVaultSecretManager(), reloadPollDelay: NoReloadDelay))
            {
                ChangeToken.OnChange(
                    () => provider.GetReloadToken(),
                    () => {
                        numOfTokensFired++;
                    });

                provider.Load();

                Assert.Equal("Value1", provider.Get("Secret1"));

                await provider.Wait();

                SetPages(client,
                        new []
                    {
                        CreateSecret("Secret1", "Value2", enabled: true, updated: updated.AddSeconds(1))
                    }
                );

                provider.Release();

                await provider.Wait();

                Assert.Equal("Value2", provider.Get("Secret1"));
                Assert.Equal(1, numOfTokensFired);
            }
        }

        [Fact]
        public async Task DoesntReloadUnchanged()
        {
            var updated = DateTime.Now;
            int numOfTokensFired = 0;

            var client = new Mock<SecretClient>();
            SetPages(client,
                new []
                {
                    CreateSecret("Secret1", "Value1", enabled: true, updated: updated)
                }
            );

            // Act & Assert
            using (var provider = new ReloadControlKeyVaultProvider(client.Object, new DefaultKeyVaultSecretManager(), reloadPollDelay: NoReloadDelay))
            {
                ChangeToken.OnChange(
                    () => provider.GetReloadToken(),
                    () => {
                        numOfTokensFired++;
                    });

                provider.Load();

                Assert.Equal("Value1", provider.Get("Secret1"));

                await provider.Wait();

                provider.Release();

                await provider.Wait();

                Assert.Equal("Value1", provider.Get("Secret1"));
                Assert.Equal(0, numOfTokensFired);
            }
        }

        [Fact]
        public async Task SupportsReloadOnRemove()
        {
            int numOfTokensFired = 0;

            var client = new Mock<SecretClient>();
            SetPages(client,
                new []
                {
                    CreateSecret("Secret1", "Value1"),
                    CreateSecret("Secret2", "Value2")
                }
            );

            // Act & Assert
            using (var provider = new ReloadControlKeyVaultProvider(client.Object, new DefaultKeyVaultSecretManager(), reloadPollDelay: NoReloadDelay))
            {
                ChangeToken.OnChange(
                    () => provider.GetReloadToken(),
                    () => {
                        numOfTokensFired++;
                    });

                provider.Load();

                Assert.Equal("Value1", provider.Get("Secret1"));

                await provider.Wait();
            
                SetPages(client,
                    new []
                    {
                        CreateSecret("Secret1", "Value2")
                    }
                );

                provider.Release();

                await provider.Wait();

                Assert.Throws<InvalidOperationException>(() => provider.Get("Secret2"));
                Assert.Equal(1, numOfTokensFired);
            }
        }

        [Fact]
        public async Task SupportsReloadOnEnabledChange()
        {
            int numOfTokensFired = 0;

            var client = new Mock<SecretClient>();
            SetPages(client,
                new []
                {
                    CreateSecret("Secret1", "Value1"),
                    CreateSecret("Secret2", "Value2")
                }
            );

            // Act & Assert
            using (var provider = new ReloadControlKeyVaultProvider(client.Object, new DefaultKeyVaultSecretManager(), reloadPollDelay: NoReloadDelay))
            {
                ChangeToken.OnChange(
                    () => provider.GetReloadToken(),
                    () => {
                        numOfTokensFired++;
                    });

                provider.Load();

                Assert.Equal("Value1", provider.Get("Secret1"));

                await provider.Wait();

                SetPages(client,
        new []
                    {
                        CreateSecret("Secret1", "Value2"),
                        CreateSecret("Secret2", "Value2", enabled: false)
                    }
                );

                provider.Release();

                await provider.Wait();

                Assert.Throws<InvalidOperationException>(() => provider.Get("Secret2"));
                Assert.Equal(1, numOfTokensFired);
            }
        }

        [Fact]
        public async Task SupportsReloadOnAdd()
        {
            int numOfTokensFired = 0;

            var client = new Mock<SecretClient>();
            SetPages(client,
                new []
                {
                    CreateSecret("Secret1", "Value1")
                }
            );

            // Act & Assert
            using (var provider = new ReloadControlKeyVaultProvider(client.Object, new DefaultKeyVaultSecretManager(), reloadPollDelay: NoReloadDelay))
            {
                ChangeToken.OnChange(
                    () => provider.GetReloadToken(),
                    () => {
                        numOfTokensFired++;
                    });

                provider.Load();

                Assert.Equal("Value1", provider.Get("Secret1"));

                await provider.Wait();

                SetPages(client,
                    new []
                    {
                        CreateSecret("Secret1", "Value1"),
                    },
                    new []
                    {
                        CreateSecret("Secret2", "Value2")
                    }
                );

                provider.Release();

                await provider.Wait();
                
                Assert.Equal("Value1", provider.Get("Secret1"));
                Assert.Equal("Value2", provider.Get("Secret2"));
                Assert.Equal(1, numOfTokensFired);
            }
        }

        [Fact]
        public void ReplaceDoubleMinusInKeyName()
        {
            var client = new Mock<SecretClient>();
            SetPages(client,
                new []
                {
                    CreateSecret("Section--Secret1", "Value1")
                }
            );

            // Act
            using (var provider = new AzureKeyVaultConfigurationProvider(client.Object, new DefaultKeyVaultSecretManager()))
            {
                provider.Load();

                // Assert
                Assert.Equal("Value1", provider.Get("Section:Secret1"));
            }
        }

        [Fact]
        public async Task LoadsSecretsInParallel()
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var expectedCount = 2;
            var client = new Mock<SecretClient>();

            SetPages(client,
                async (string id) =>
                {
                    if (Interlocked.Decrement(ref expectedCount) == 0)
                    {
                        tcs.SetResult(null);
                    }

                    await tcs.Task.TimeoutAfter(TimeSpan.FromSeconds(10));
                },
                new[]
                {
                    CreateSecret("Secret1", "Value1"),
                    CreateSecret("Secret2", "Value2")
                }
            );

            // Act
            var provider = new AzureKeyVaultConfigurationProvider(client.Object, new DefaultKeyVaultSecretManager());
            provider.Load();
            await tcs.Task;

            // Assert
            Assert.Equal("Value1", provider.Get("Secret1"));
            Assert.Equal("Value2", provider.Get("Secret2"));
        }

        [Fact]
        public void ConstructorThrowsForNullManager()
        {
            Assert.Throws<ArgumentNullException>(() => new AzureKeyVaultConfigurationProvider(Mock.Of<SecretClient>(), null));
        }

        [Fact]
        public void ConstructorThrowsForZeroRefreshPeriodValue()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AzureKeyVaultConfigurationProvider(Mock.Of<SecretClient>(), new DefaultKeyVaultSecretManager(), TimeSpan.Zero));
        }

        [Fact]
        public void ConstructorThrowsForNegativeRefreshPeriodValue()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AzureKeyVaultConfigurationProvider(Mock.Of<SecretClient>(), new DefaultKeyVaultSecretManager(), TimeSpan.FromMilliseconds(-1)));
        }

        [Fact]
        public override void Null_values_are_included_in_the_config()
        {
            AssertConfig(BuildConfigRoot(LoadThroughProvider(TestSection.NullsTestConfig)), expectNulls: true);
        }

        private class EndsWithOneKeyVaultSecretManager : DefaultKeyVaultSecretManager
        {
            public override bool Load(SecretProperties secret)
            {
                return secret.Name.EndsWith("1");
            }
        }

        private class ReloadControlKeyVaultProvider : AzureKeyVaultConfigurationProvider
        {
            private TaskCompletionSource<object> _releaseTaskCompletionSource = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            private TaskCompletionSource<object> _signalTaskCompletionSource = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            public ReloadControlKeyVaultProvider(SecretClient client, IKeyVaultSecretManager manager, TimeSpan? reloadPollDelay = null) : base(client, manager, reloadPollDelay)
            {
            }

            protected override async Task WaitForReload()
            {
                _signalTaskCompletionSource.SetResult(null);
                await _releaseTaskCompletionSource.Task.TimeoutAfter(TimeSpan.FromSeconds(10));
            }

            public async Task Wait()
            {
                await _signalTaskCompletionSource.Task.TimeoutAfter(TimeSpan.FromSeconds(10));
            }

            public void Release()
            {
                if (!_signalTaskCompletionSource.Task.IsCompleted)
                {
                    throw new InvalidOperationException("Provider is not waiting for reload");
                }

                var releaseTaskCompletionSource = _releaseTaskCompletionSource;
                _releaseTaskCompletionSource = new TaskCompletionSource<object>();
                _signalTaskCompletionSource = new TaskCompletionSource<object>();
                releaseTaskCompletionSource.SetResult(null);
            }
        }

        protected override (IConfigurationProvider Provider, Action Initializer) LoadThroughProvider(TestSection testConfig)
        {   
            var values = new List<KeyValuePair<string, string>>();
            SectionToValues(testConfig, "", values);

            var client = new Mock<SecretClient>();
            SetPages(client, values.Select(kvp=>CreateSecret(kvp.Key, kvp.Value)).ToArray());

            return (new AzureKeyVaultConfigurationProvider(client.Object, new DefaultKeyVaultSecretManager()), () => {});
        }
    }
}