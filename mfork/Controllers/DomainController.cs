using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using ACMESharp.Authorizations;
using ACMESharp.Protocol;
using ACMESharp.Protocol.Resources;
using AirtableApiClient;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Management.AppService.Fluent.Models;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Rest.TransientFaultHandling;
using Newtonsoft.Json;
using PKISharp.SimplePKI;

namespace mfork.Controllers
{
    [Route("domains")]
    [ApiController]
    public class DomainController : ControllerBase
    {
        private readonly IConfiguration _configuration;

        public DomainController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        [HttpPost]
        public async Task<IActionResult> Post([FromBody] DomainRequest request)
        {
            if (!IsValidDomain(request.Domain))
                return BadRequest(new DomainResponse { Error = "Invalid domain" });

            var domain = string.Join(".", request.Domain.Split(".").TakeLast(2));
            var subDomain = string.Join(".", request.Domain.Split(".").SkipLast(2));

            var credentials = new AzureCredentialsFactory()
                .FromServicePrincipal(
                    _configuration["Azure:ClientId"],
                    _configuration["Azure:ClientSecret"],
                    _configuration["Azure:TenantId"],
                    AzureEnvironment.AzureGlobalCloud
                );

            var azure = Azure
                .Configure()
                .WithRetryPolicy(new RetryPolicy(new TransientErrorIgnoreStrategy(), 0))
                .Authenticate(credentials)
                .WithSubscription(_configuration["Azure:SubscriptionId"]);

            var webApp = await azure.AppServices.WebApps.GetByIdAsync(
                _configuration["Azure:AppId"]);

            try
            {
                webApp.Update()
                    .DefineHostnameBinding()
                    .WithThirdPartyDomain(domain)
                    .WithSubDomain(subDomain)
                    .WithDnsRecordType(CustomHostNameDnsRecordType.CName)
                    .Attach()
                    .Apply();
            }
            catch (Exception e)
            {
                return BadRequest(new DomainResponse { Error = "Unable to validate domain ownership" });
            }

            _ = Task.Run(async () =>
            {
                using var airtableBase = new AirtableBase(_configuration["Airtable:Key"], _configuration["Airtable:Base"]);
                try
                {
                    HttpClient httpClient = new HttpClient { BaseAddress = new Uri(_configuration["Acme:Endpoint"]) };
                    AcmeProtocolClient acme = new AcmeProtocolClient(httpClient, usePostAsGet: true);

                    var acmeDir = await acme.GetDirectoryAsync();
                    acme.Directory = acmeDir;

                    await acme.GetNonceAsync();

                    var account = await acme.CreateAccountAsync(new[] { "mailto:" + _configuration["Acme:Email"] }, true);
                    acme.Account = account;

                    var order = await acme.CreateOrderAsync(new[] { request.Domain });
                    if (order.Payload.Status == "invalid")
                    {
                        return;
                    }

                    var authorizationUrl = order.Payload.Authorizations.FirstOrDefault();
                    if (string.IsNullOrEmpty(authorizationUrl))
                    {
                        return;
                    }
                    var authorization = await acme.GetAuthorizationDetailsAsync(authorizationUrl);

                    foreach (var challenge in authorization.Challenges.Where(x => x.Type == "http-01").ToList())
                    {
                        var challengeValidationDetails = (Http01ChallengeValidationDetails)
                            AuthorizationDecoder.DecodeChallengeValidation(authorization, challenge.Type, acme.Signer);

                        var path = challengeValidationDetails.HttpResourcePath;
                        var token = path.Split("/", StringSplitOptions.RemoveEmptyEntries).Last();
                        var value = challengeValidationDetails.HttpResourceValue;
                        var contentType = challengeValidationDetails.HttpResourceContentType;

                        await airtableBase.CreateRecord("Acme", new Fields
                        {
                            FieldsCollection = new Dictionary<string, object>
                            {
                                ["Token"] = token,
                                ["Value"] = value,
                                ["ContentType"] = contentType
                            }
                        });

                        await Task.Delay(10 * 1000);
                        var challengeUpdated = await acme.AnswerChallengeAsync(challenge.Url);
                    }

                    //Wait for challenge to be resolved
                    var waitUntil = DateTime.Now.AddSeconds(300);
                    Authorization authorizationUpdated;
                    do
                    {
                        await Task.Delay(10 * 1000);
                        authorizationUpdated = await acme.GetAuthorizationDetailsAsync(authorizationUrl);
                    } while (authorizationUpdated.Status != "valid" && DateTime.Now < waitUntil);

                    if (authorizationUpdated.Status != "valid")
                    {
                        return;
                    }

                    //Generate certificate private key and CSR (Certificate signing request)
                    var keyPair = PkiKeyPair.GenerateEcdsaKeyPair(256);
                    var csr = new PkiCertificateSigningRequest($"CN={request.Domain}", keyPair, PkiHashAlgorithm.Sha256);
                    var certCsr = csr.ExportSigningRequest(PkiEncodingFormat.Der);

                    order = await acme.FinalizeOrderAsync(order.Payload.Finalize, certCsr);
                    if (order.Payload.Status != "valid")
                    {
                        return;
                    }

                    if (string.IsNullOrEmpty(order.Payload.Certificate))
                    {
                        //Wait for certificate
                        var waitUntil2 = DateTime.Now.AddSeconds(300);
                        while (DateTime.Now < waitUntil2)
                        {
                            await Task.Delay(10 * 1000);
                            order = await acme.GetOrderDetailsAsync(order.OrderUrl, existing: order);

                            if (!string.IsNullOrEmpty(order.Payload.Certificate))
                                break;
                        }
                    }
                    if (string.IsNullOrEmpty(order.Payload.Certificate))
                    {
                        return;
                    }

                    var certResp = await acme.GetAsync(order.Payload.Certificate);
                    if (!certResp.IsSuccessStatusCode)
                    {
                        return;
                    }

                    var certByteArray = await certResp.Content.ReadAsByteArrayAsync();

                    //Export PFX file
                    var pfxPassword = _configuration["Acme:PfxPassword"];
                    var privateKey = keyPair.PrivateKey;

                    using var cert = new X509Certificate2(certByteArray);
                    
                    X509Chain chain = new X509Chain();
                    chain.Build(cert);
                    List<PkiCertificate> chainList = new List<PkiCertificate>();
                    foreach (var e in chain.ChainElements)
                    {
                        chainList.Add(PkiCertificate.From(e.Certificate));
                    }

                    var pfx = chainList[0].Export(PkiArchiveFormat.Pkcs12,
                        chain: chainList.Skip(1),
                        privateKey: privateKey,
                        password: pfxPassword?.ToCharArray());

                    webApp.Update()
                        .DefineSslBinding()
                        .ForHostname(request.Domain)
                        .WithPfxByteArrayToUpload(pfx, pfxPassword)
                        .WithSniBasedSsl()
                        .Attach()
                        .Apply();
                }
                catch (Exception e)
                {
                    await airtableBase.CreateRecord("Logs", new Fields
                    {
                        FieldsCollection = new Dictionary<string, object>
                        {
                            ["Hostname"] = request.Domain,
                            ["Event"] = "exception-thrown",
                            ["Data"] = JsonConvert.SerializeObject(e)
                        }
                    });
                }
            });
            
            

            return Ok(new DomainResponse { IsSuccessful = true });
        }

        private static bool IsValidDomain(string domain)
        {
            return !(string.IsNullOrEmpty(domain)
                     || Uri.CheckHostName(domain) == UriHostNameType.Unknown
                     || domain.Split(".").Count() <= 2
                     || domain == "mfork.azurewebsites.net");
        }

        [HttpPost("validate")]
        public IActionResult Validate([FromBody]
            DomainValidationRequest request)
        {
            if (IsValidDomain(request.BaseDomain) && IsValidDomain(request.MobileDomain) &&
                IsValidDomain(request.DesktopDomain))
                return Ok(new DomainResponse { IsSuccessful = true });
            
            return BadRequest();
        }
    }

    public class DomainValidationRequest
    {
        public string BaseDomain { get; set; }
        public string MobileDomain { get; set; }
        public string DesktopDomain { get; set; }
    }

    public class DomainRequest
    {
        public string Domain { get; set; }
    }

    public class DomainResponse
    {
        public bool IsSuccessful { get; set; }
        public string Error { get; set; }
    }
}