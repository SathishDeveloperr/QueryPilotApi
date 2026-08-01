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

                policy.WithOrigins("https://query-pilot-five.vercel.app")
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();

            });
        });


// Mongo client is thread-safe -> singleton is the recommended lifetime.
builder.Services.AddSingleton<IMongoClient>(_ =>
    new MongoClient(builder.Configuration["Mongo:ConnectionString"] ?? "mongodb://localhost:27017"));

builder.Services.AddSingleton<QueryGuard>();
builder.Services.AddHttpClient<LlmClient>();
builder.Services.AddSingleton<NlQueryService>();

var app = builder.Build();

// Seed demo data on startup (no-op if data already exists).
await SeedData.EnsureSeededAsync(
    app.Services.GetRequiredService<IMongoClient>(),
    builder.Configuration["Mongo:Database"] ?? "querypilot");

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("frontend");
app.MapControllers();

app.Run();
