namespace Model
{
    public struct SBinData
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

    public struct ParsedCsvFileInfo
    {
        public FileInfo OriginalFile { get; }
        public string WaferId { get; }
        public string RpValue { get; }
        public DateTime FileTimestamp { get; }
        public string MergeKey { get; }

        public ParsedCsvFileInfo(FileInfo originalFile, string waferId, string rpValue, DateTime fileTimestamp, string mergeKey)
        {
            OriginalFile = originalFile;
            WaferId = waferId;
            RpValue = rpValue;
            FileTimestamp = fileTimestamp;
            MergeKey = mergeKey;
        }
    }
}
