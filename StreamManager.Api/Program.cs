using Microsoft.EntityFrameworkCore;
using StreamManager.Api.Configuration;
using StreamManager.Api.Data;
using StreamManager.Api.Services;
using StreamManager.Api.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Bind configuration
builder.Services.Configure<ResourceLimitsOptions>(
    builder.Configuration.GetSection(ResourceLimitsOptions.SectionName));

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowWebApp", policy =>
    {
        policy.WithOrigins("https://localhost:7122", "http://localhost:5201") // Web app URLs
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// Configure SignalR
builder.Services.AddSignalR();

// Configure Entity Framework
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Host=localhost;Database=stream_manager_db;Username=admin;Password=password";
builder.Services.AddDbContext<StreamManagerDbContext>(options =>
    options.UseNpgsql(connectionString));

// Configure HttpClient for ksqlDB
builder.Services.AddHttpClient<AdHocKsqlService>(client =>
{
    client.DefaultRequestVersion = new Version(2, 0);
    client.Timeout = TimeSpan.FromMinutes(30); // Long timeout for streaming
});

// Register custom services
builder.Services.AddScoped<AdHocKsqlService>();
builder.Services.AddSingleton<KsqlQueryValidator>();
builder.Services.AddSingleton<QueryRateLimiter>();
builder.Services.AddHostedService<TopicProxyService>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseDeveloperExceptionPage();
}

// Apply database migrations
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<StreamManagerDbContext>();
    try
    {
        await context.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while migrating the database");
    }
}

app.UseHttpsRedirection();
app.UseCors("AllowWebApp");

app.UseRouting();

app.MapControllers();
app.MapHub<KsqlHub>("/hub/ksql");

app.Run();
