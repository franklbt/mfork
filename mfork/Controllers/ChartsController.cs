using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AirtableApiClient;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace mfork.Controllers
{
    [Route("charts")]
    [ApiController]
    public class ChartsController : Controller
    {
        private readonly IConfiguration _configuration;

        public ChartsController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        [HttpGet("{domainId}/repartition")]
        public async Task<IActionResult> GetRepartitionChart(string domainId)
        {
            using var airtableBase = new AirtableBase(_configuration["Airtable:Key"], _configuration["Airtable:Base"]);
            var redirectedToField = "RedirectedTo";
            var records = await airtableBase.ListRecords("Views",
                fields: new[] { redirectedToField },
                filterByFormula: "{BaseDomain}='" + domainId + "'");
            var mobileCount = records.Records.Count(x => x.Fields[redirectedToField].ToString() == "Mobile");
            var desktopCount = records.Records.Count(x => x.Fields[redirectedToField].ToString() == "Desktop");
            return Redirect(
                $"https://quickchart.io/chart?bkg=white&c=%7B%0A%20%20type%3A%20%27doughnut%27%2C%0A%20%20data%3A%20%7B%0A%20%20%20%20datasets%3A%20%5B%0A%20%20%20%20%20%20%7B%0A%20%20%20%20%20%20%20%20data%3A%20%5B{mobileCount}%2C%20{desktopCount}%5D%2C%0A%20%20%20%20%20%20%20%20backgroundColor%3A%20%5B%0A%20%20%20%20%20%20%20%20%20%20%27%23A83400%27%2C%0A%20%20%20%20%20%20%20%20%20%20%27rgb(255%2C%20159%2C%2064)%27%2C%0A%20%20%20%20%20%20%20%20%5D%2C%0A%20%20%20%20%20%20%20%20label%3A%20%27Dataset%201%27%2C%0A%20%20%20%20%20%20%7D%2C%0A%20%20%20%20%5D%2C%0A%20%20%20%20labels%3A%20%5B%27Mobile%27%2C%20%27Desktop%27%5D%2C%0A%20%20%7D%0A%7D%0A");
        }

        [HttpGet("{domainId}/devices/{deviceId}")]
        public async Task<IActionResult> GetCharts(string domainId, DeviceId deviceId)
        {
            using var airtableBase = new AirtableBase(_configuration["Airtable:Key"], _configuration["Airtable:Base"]);
            var redirectedToField = "RedirectedTo";
            var creationTimeField = "CreationTime";
            var records = await airtableBase.ListRecords("Views",
                fields: new[] { redirectedToField, creationTimeField },
                filterByFormula: "AND({BaseDomain}='" + domainId + "', {RedirectedTo}='" +
                                 (deviceId == DeviceId.Mobile ? "Mobile" : "Desktop") + "')");
            var typed = records.Records.Select(x =>
            {
                return new
                {
                    RedirectedTo = x.Fields[redirectedToField],
                    CreationDate = (DateTime)x.Fields[creationTimeField]
                };
            });
            var groupedByDay = typed
                .GroupBy(x => x.CreationDate.Date)
                .Select(x => new
                {
                    Date = x.Key,
                    Count = x.Count()
                })
                .ToList();
            var labelStr = string.Join("%2C", groupedByDay.Select(x => "%22" +  x.Date.ToString("d") + "%22"));
            var dataStr = string.Join("%2C", groupedByDay.Select(x => x.Count.ToString()));
            return Redirect(
                $"https://quickchart.io/chart?bkg=white&c=%7B%0A%20%20%22type%22%3A%20%22bar%22%2C%0A%20%20%22data%22%3A%20%7B%0A%20%20%20%20%22labels%22%3A%20%5B%0A%20%20%20%20%20%20{labelStr}%0A%20%20%20%20%5D%2C%0A%20%20%20%20%22datasets%22%3A%20%5B%0A%20%20%20%20%20%20%7B%0A%20%20%20%20%20%20%20%20%22label%22%3A%20%22Redirections%22%2C%0A%20%20%20%20%20%20%20%20%22backgroundColor%22%3A%20%22%23A83400%22%2C%0A%20%20%20%20%20%20%20%20%22data%22%3A%20%5B%0A%20%20%20%20%20%20%20%20%20%20{dataStr}%0A%20%20%20%20%20%20%20%20%5D%0A%20%20%20%20%20%20%7D%0A%20%20%20%20%5D%0A%20%20%7D%0A%7D");
        }
    }

    public enum DeviceId
    {
        Mobile,
        Desktop
    }
}