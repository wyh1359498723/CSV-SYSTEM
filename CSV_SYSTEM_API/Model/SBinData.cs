namespace CSV_SYSTEM_API.Model
{
    public class SBinData
    {
        public long Count { get; set; }
        public string Description { get; set; }
        public int SiteId { get; set; }

        public SBinData(long count, string description, int siteId)
        {
            Count = count;
            Description = description;
            SiteId = siteId;
        }
    }
}
