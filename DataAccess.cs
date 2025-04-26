// BillingRecordService.cs
public class BillingRecordService
{
    private readonly CosmosClient _cosmosClient;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _databaseId;
    private readonly string _containerId;
    private readonly string _blobContainerName;

    public BillingRecordService(
        CosmosClient cosmosClient, 
        BlobServiceClient blobServiceClient,
        string databaseId, 
        string containerId,
        string blobContainerName)
    {
        _cosmosClient = cosmosClient;
        _blobServiceClient = blobServiceClient;
        _databaseId = databaseId;
        _containerId = containerId;
        _blobContainerName = blobContainerName;
    }

    // This is the method that your API calls - no change needed in API contracts
    public async Task<BillingRecord> GetBillingRecordAsync(string recordId)
    {
        // First check the lookup table to determine where the record is stored
        var lookupContainer = _cosmosClient.GetContainer(_databaseId, "lookup");
        var lookupResponse = await lookupContainer.ReadItemAsync<LookupEntry>(
            recordId, 
            new PartitionKey(recordId));
        
        var lookupEntry = lookupResponse.Resource;
        
        // Based on the lookup information, retrieve from appropriate storage
        if (lookupEntry.StorageLocation == "cosmos")
        {
            // Record is in Cosmos DB
            var container = _cosmosClient.GetContainer(_databaseId, _containerId);
            var response = await container.ReadItemAsync<BillingRecord>(
                recordId, 
                new PartitionKey(recordId));
            
            return response.Resource;
        }
        else // "blob"
        {
            // Record is in Blob Storage
            var blobContainer = _blobServiceClient.GetBlobContainerClient(_blobContainerName);
            var blobClient = blobContainer.GetBlobClient(lookupEntry.BlobPath);
            
            // Download the blob content
            BlobDownloadInfo download = await blobClient.DownloadAsync();
            
            using (var streamReader = new StreamReader(download.Content))
            {
                var json = await streamReader.ReadToEndAsync();
                return JsonConvert.DeserializeObject<BillingRecord>(json);
            }
        }
    }
    
    // Continue using the existing method for saving new records
    public async Task SaveBillingRecordAsync(BillingRecord record)
    {
        // Save record to Cosmos DB
        var container = _cosmosClient.GetContainer(_databaseId, _containerId);
        await container.CreateItemAsync(record, new PartitionKey(record.Id));
        
        // Create or update the lookup entry
        var lookupContainer = _cosmosClient.GetContainer(_databaseId, "lookup");
        var lookupEntry = new LookupEntry
        {
            Id = record.Id,
            RecordType = "billing",
            CreatedDate = record.CreatedDate,
            StorageLocation = "cosmos",
            BlobPath = null
        };
        
        await lookupContainer.UpsertItemAsync(lookupEntry, new PartitionKey(lookupEntry.Id));
    }
}

public class LookupEntry
{
    [JsonProperty("id")]
    public string Id { get; set; }
    
    [JsonProperty("recordType")]
    public string RecordType { get; set; }
    
    [JsonProperty("createdDate")]
    public DateTime CreatedDate { get; set; }
    
    [JsonProperty("storageLocation")]
    public string StorageLocation { get; set; }  // "cosmos" or "blob"
    
    [JsonProperty("blobPath")]
    public string BlobPath { get; set; }  // null if in cosmos
}

public class BillingRecord
{
    [JsonProperty("id")]
    public string Id { get; set; }
    
    [JsonProperty("createdDate")]
    public DateTime CreatedDate { get; set; }
    
    // Other billing record properties
    // ...
}
