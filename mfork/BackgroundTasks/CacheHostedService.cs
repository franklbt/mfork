using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AirtableApiClient;
using mfork.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace mfork.BackgroundTasks
{
    public class CacheHostedService : IHostedService, IDisposable
    {
        private readonly ILogger<CacheHostedService> _logger;
        private readonly IConfiguration _configuration;
        private Timer _timer;
        private IMemoryCache _memoryCache;

        public CacheHostedService(ILogger<CacheHostedService> logger,
            IConfiguration configuration, 
            IMemoryCache memoryCache)
        {
            _logger = logger;
            _configuration = configuration;
            _memoryCache = memoryCache;
        }

        public Task StartAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Render Hosted Service running.");

            _timer = new Timer(Render, null, TimeSpan.Zero,
                TimeSpan.FromHours(24));

            return Task.CompletedTask;
        }

        private async void Render(object state)
        {
            using var airtableBase =
                new AirtableBase(_configuration["Airtable:Key"], _configuration["Airtable:Base"]);
            var response = await airtableBase.ListRecords(
                "Redirections",
                view: "Main", maxRecords: 1);

            if (!response.Success)
                return;

            var records = response.Records
                .ToDictionary(x => x.Fields["Base Address"].ToString(),
                    x => new DomainMappingValue
                    {
                        Desktop = new DomainVariantValue {Url = x.Fields["Desktop Redirection"].ToString()},
                        Mobile = new DomainVariantValue {Url = x.Fields["Mobile Redirection"].ToString()}
                    });

            foreach (var record in records)
            {
                record.Value.Mobile.Html = await FillHtmlCache("http://" + record.Value.Mobile.Url);
                record.Value.Desktop.Html = await FillHtmlCache("http://" + record.Value.Desktop.Url);
                _memoryCache.Set(record.Key, record.Value);
            }

        }

        private async Task<string> FillHtmlCache(string address)
        {
            Browser browser = null;
            try
            {
                browser = await Puppeteer.ConnectAsync(new ConnectOptions
                {
                    BrowserWSEndpoint = "wss://chrome.browserless.io?token=" + _configuration["Browserless:Token"]
                });
                var page = await browser.NewPageAsync();
                await page.GoToAsync(address);
                await page.WaitForNavigationAsync();
                await page.EvaluateExpressionAsync<string>(
                    $"const base = document.createElement('base'); base.href = '{address}'; document.head.prepend(base);" +
                    "const elements = document.querySelectorAll('script, link[rel=\"import\"]'); elements.forEach(e => e.remove());" +
                    "const cssText = Array.from(document.querySelector('#react-native-stylesheet').sheet.cssRules).reduce((prev, cssRule) => prev + cssRule.cssText, '');" +
                    "const style = document.createElement('style'); style.innerText = cssText; document.head.prepend(style);");
                var content = await page.GetContentAsync();
                return content;
            }
            finally
            {
                if (browser != null)
                {
                    await browser.CloseAsync();
                }
            }
        }

        public Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Render Hosted Service is stopping.");

            _timer?.Change(Timeout.Infinite, 0);

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}