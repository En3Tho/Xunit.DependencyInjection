﻿using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Xunit.DependencyInjection.Logging
{
    public class XunitTestOutputLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentDictionary<string, ILogger> _loggers = new();
        private readonly ITestOutputHelperAccessor _accessor;
        private readonly Func<string, LogLevel, bool> _filter;

        /// <summary>Log minLevel LogLevel.Information</summary>
        public XunitTestOutputLoggerProvider(ITestOutputHelperAccessor accessor) : this(accessor, (name, level) => level is >= LogLevel.Information and < LogLevel.None) { }

        public XunitTestOutputLoggerProvider(ITestOutputHelperAccessor accessor, Func<string, LogLevel, bool> filter)
        {
            _accessor = accessor;
            _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        }

        public void Dispose() { }

        public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new XunitTestOutputLogger(_accessor, name, _filter));

        public static void Register(IServiceProvider provider) =>
            provider.GetRequiredService<ILoggerFactory>().AddProvider(ActivatorUtilities.CreateInstance<XunitTestOutputLoggerProvider>(provider));
    }
}
