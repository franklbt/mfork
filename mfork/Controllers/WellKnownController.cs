using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AirtableApiClient;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;

namespace mfork.Controllers
{
    [Route(".well-known")]
    [ApiController]
    public class WellKnownController : ControllerBase
    {

        private readonly IConfiguration _configuration;

        public WellKnownController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        [HttpGet("acme-challenge/{token}")]
        public async Task<IActionResult> AcmeChallenge([FromRoute] string token)
        {

            using (AirtableBase airtableBase = new AirtableBase(_configuration["Airtable:Key"], _configuration["Airtable:Base"]))
            {
                var response = await airtableBase.ListRecords(
                   "Acme", fields: new[] { "Value", "ContentType" }, filterByFormula: "{Token} = '" + token + "'", view: "Main", maxRecords: 1);

                if (response.Success)
                {
                    var airtableRecord = response.Records.FirstOrDefault();
                    if (airtableRecord != null)
                    {
                        return Content(airtableRecord.Fields["Value"].ToString(),
                            new MediaTypeHeaderValue(airtableRecord.Fields["ContentType"].ToString()));
                    }
                }
            }

            return NotFound();
        }
    }
}
