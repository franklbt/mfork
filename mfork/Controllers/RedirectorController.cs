using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AirtableApiClient;
using mfork.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Net.Http.Headers;
using PuppeteerSharp;
using Wangkanai.Detection;
using Browser = PuppeteerSharp.Browser;

namespace mfork.Controllers
{
    [Route("")]
    [ApiController]
    public class RedirectorController : ControllerBase
    {
        private readonly IDeviceResolver _deviceResolver;
        private readonly ICrawlerResolver _crawlerResolver;
        private readonly IMemoryCache _memoryCache;
        private readonly IConfiguration _configuration;

        public RedirectorController(IConfiguration configuration,
            IDeviceResolver deviceResolver,
            ICrawlerResolver crawlerResolver,
            IMemoryCache memoryCache)
        {
            _deviceResolver = deviceResolver;
            _crawlerResolver = crawlerResolver;
            _memoryCache = memoryCache;
            _configuration = configuration;
        }

        public async Task<IActionResult> Get()
        {
            var host = Request.Host.ToString();
            var isMobile = _deviceResolver.Device.Type == DeviceType.Mobile;
            var isCrawler = _crawlerResolver.Crawler != null;
            var type = isMobile ? "Mobile Redirection" : "Desktop Redirection";
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var userAgent = Request.Headers["User-Agent"].ToString();
            var referer = Request.Headers["Referer"].ToString();
            var hasEntry = _memoryCache.TryGetValue<DomainMappingValue>(host, out var value);
            
            _ = Task.Run(async () =>
            {
                using var airtableBaseInTask =
                    new AirtableBase(_configuration["Airtable:Key"], _configuration["Airtable:Base"]);
                await airtableBaseInTask.CreateRecord("Views", new Fields
                {
                    FieldsCollection = new Dictionary<string, object>
                    {
                        ["BaseDomain"] = host,
                        ["Ip"] = ip,
                        ["UserAgent"] = userAgent,
                        ["RedirectedTo"] = isMobile ? "Mobile" : "Desktop",
                        ["Referrer"] = referer
                    }
                });
            });
            
            if (isCrawler && hasEntry)
            {
                var content = isMobile ? value.Mobile.Html : value.Desktop.Html;
                return Content(content, "text/html");
            }
            else
            {
                using var airtableBase =
                    new AirtableBase(_configuration["Airtable:Key"], _configuration["Airtable:Base"]);
                var response = await airtableBase.ListRecords(
                    "Redirections", fields: new[] {type}, filterByFormula: "{Base Address} = '" + host + "'",
                    view: "Main", maxRecords: 1);

                if (!response.Success) return BadRequest();

                var airtableRecord = response.Records.FirstOrDefault();
                if (airtableRecord == null) return BadRequest();
                var address = "https://" + airtableRecord.Fields[type];
                
                return base.Redirect(address);
            }
        }
    }
}