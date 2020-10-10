﻿using Microsoft.Extensions.DependencyInjection;
using System;

namespace Vertex.Runtime.Core
{
    public class ProviderFixture : IDisposable
    {
        public ProviderFixture()
        {
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddVertex();
            serviceCollection.AddLogging();
            Provider = serviceCollection.BuildServiceProvider();
        }

        public void Dispose()
        {
            Provider.Dispose();
        }

        public ServiceProvider Provider { get; private set; }
    }
}
