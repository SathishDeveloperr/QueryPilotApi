using MongoDB.Bson;
using MongoDB.Driver;

namespace QueryPilot.Api.Services;

/// <summary>Seeds sample sales data on first run so the demo works instantly.</summary>
public static class SeedData
{
    public static async Task EnsureSeededAsync(IMongoClient client, string dbName)
    {
        var db = client.GetDatabase(dbName);
        var customers = db.GetCollection<BsonDocument>("customers");

        if (await customers.EstimatedDocumentCountAsync() > 0) return; // already seeded

        var names = new[]
        {
            ("Zenith Retail", "Chennai", "Enterprise"), ("Kaveri Foods", "Coimbatore", "SMB"),
            ("Nova Textiles", "Tiruppur", "SMB"), ("BlueWave Logistics", "Mumbai", "Enterprise"),
            ("Everest Pharma", "Hyderabad", "Enterprise"), ("Lotus Interiors", "Bengaluru", "Consumer"),
            ("Ganga Traders", "Varanasi", "SMB"), ("Skyline Estates", "Pune", "Consumer"),
            ("Meridian Motors", "Chennai", "Enterprise"), ("Palmgrove Resorts", "Kochi", "SMB"),
        };
        var rng = new Random(42); // fixed seed = same demo data every install

        await customers.InsertManyAsync(names.Select((c, i) => new BsonDocument
        {
            ["name"] = c.Item1,
            ["city"] = c.Item2,
            ["segment"] = c.Item3,
            ["joined"] = DateTime.UtcNow.AddDays(-rng.Next(100, 900)),
        }));

        var products = new[]
        {
            ("Rack Server", "Hardware", 180000), ("Firewall Appliance", "Hardware", 95000),
            ("CRM License", "Software", 45000), ("Analytics Suite", "Software", 120000),
            ("Cloud Migration", "Services", 250000), ("Annual Support", "Services", 60000),
        };
        var statuses = new[] { "Completed", "Completed", "Completed", "Pending", "Cancelled" };

        var orders = new List<BsonDocument>();
        for (var i = 0; i < 120; i++)
        {
            var p = products[rng.Next(products.Length)];
            orders.Add(new BsonDocument
            {
                ["customerName"] = names[rng.Next(names.Length)].Item1,
                ["product"] = p.Item1,
                ["category"] = p.Item2,
                ["amount"] = p.Item3 + rng.Next(-10000, 25000),
                ["status"] = statuses[rng.Next(statuses.Length)],
                ["orderDate"] = DateTime.UtcNow.AddDays(-rng.Next(0, 365)),
            });
        }
        await db.GetCollection<BsonDocument>("orders").InsertManyAsync(orders);
    }
}
