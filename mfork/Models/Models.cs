namespace mfork.Models
{
    public class DomainMappingValue
    {
        public DomainVariantValue Mobile { get; set; }
        public DomainVariantValue Desktop { get; set; }
    }

    public class DomainVariantValue
    {
        public string Url { get; set; }
        public string Html { get; set; }
    }
}