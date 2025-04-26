// DataMigrationFunction.cs
public class DataMigrationFunction
{
    private readonly CosmosClient _cosmosClient;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly ILogger<DataMigrationFunction> _logger;
    private readonly string _databaseId;
    private readonly string _containerId;
    private readonly string _blobContainerName;

    public DataMigrationFunction(
        CosmosClient cosmosClient,
        BlobServiceClient blobServiceClient,
        ILogger<DataMigrationFunction> logger,
        string databaseId,
        string containerId,
        string blobContainerName)
    {
        _cosmosClient = cosmosClient;
        _blobServiceClient = blobServiceClient;
        _logger = logger;
        _databaseId = databaseId;
        _containerId = containerId;
        _blobContainerName = blobContainerName;
    }

    // Runs on a schedule (e.g., daily)
    [FunctionName("MigrateOldRecords")]
    public async Task Run([TimerTrigger("0 0 0 * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Starting data migration process");
        
        // Calculate date threshold (3 months ago)
        var thresholdDate = DateTime.UtcNow.AddMonths(-3);
        
        // Get records from Cosmos DB that are older than the threshold
        var container = _cosmosClient.GetContainer(_databaseId, _containerId);
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.createdDate < @thresholdDate AND c.recordType = 'billing'")
            .WithParameter("@thresholdDate", thresholdDate);
        
        var blobContainer = _blobServiceClient.GetBlobContainerClient(_blobContainerName);
        
        // Ensure blob container exists
        await blobContainer.CreateIfNotExistsAsync();
        
        // Get lookup container
        var lookupContainer = _cosmosClient.GetContainer(_databaseId, "lookup");
        
        // Process records in batches to avoid memory issues
        using (var iterator = container.GetItemQueryIterator<BillingRecord>(query, 
                   requestOptions: new QueryRequestOptions { MaxItemCount = 100 }))
        {
            while (iterator.HasMoreResults)
            {
                var batch = await iterator.ReadNextAsync();
                foreach (var record in batch)
                {
                    try
                    {
                        // Define blob path based on creation date
                        var blobPath = $"billing/{record.CreatedDate.Year}/{record.CreatedDate.Month:D2}/{record.Id}.json";
                        var blobClient = blobContainer.GetBlobClient(blobPath);
                        
                        // Convert record to JSON and upload to blob
                        var json = JsonConvert.SerializeObject(record);
                        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                        {
                            await blobClient.UploadAsync(stream, overwrite: true);
                        }
                        
                        // Update lookup entry
                        var lookupEntry = new LookupEntry
                        {
                            Id = record.Id,
                            RecordType = "billing",
                            CreatedDate = record.CreatedDate,
                            StorageLocation = "blob",
                            BlobPath = blobPath
                        };
                        
                        await lookupContainer.UpsertItemAsync(lookupEntry, new PartitionKey(lookupEntry.Id));
                        
                        // Delete the record from Cosmos DB (optional - depends on your requirements)
                        await container.DeleteItemAsync<BillingRecord>(record.Id, new PartitionKey(record.Id));
                        
                        _logger.LogInformation($"Migrated record {record.Id} to blob storage");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error migrating record {record.Id}");
                    }
                }
            }
        }
        
        _logger.LogInformation("Data migration process completed");
    }
}
