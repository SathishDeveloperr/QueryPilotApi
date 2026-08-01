using MongoDB.Driver;
using QueryPilot.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        policy.WithOrigins("https://query-pilot-five.vercel.app", "http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Mongo client is thread-safe -> singleton is the recommended lifetime.
builder.Services.AddSingleton<IMongoClient>(_ =>
    new MongoClient(builder.Configuration["Mongo:ConnectionString"] ?? "mongodb://localhost:27017"));

// Reads the real collections/fields out of the database so the LLM never invents a name.
builder.Services.AddSingleton<SchemaProvider>();
builder.Services.AddSingleton<QueryGuard>();
builder.Services.AddHttpClient<LlmClient>();

// Scoped, not singleton: AddHttpClient registers LlmClient as transient, and a singleton
// consumer would pin one HttpMessageHandler for the whole process lifetime, defeating
// IHttpClientFactory's handler rotation. Its other dependencies are singletons.
builder.Services.AddScoped<NlQueryService>();

var app = builder.Build();

// Demo seeding is OFF by default: this writes sample "customers" and 120 sample
// "orders" documents, which must never happen against a real database.
// Enable only for a throwaway demo DB via "Seed:Enabled": true.
if (builder.Configuration.GetValue("Seed:Enabled", false))
{
    await SeedData.EnsureSeededAsync(
        app.Services.GetRequiredService<IMongoClient>(),
        builder.Configuration["Mongo:Database"] ?? "querypilot");
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("frontend");
app.MapControllers();

app.Run();
