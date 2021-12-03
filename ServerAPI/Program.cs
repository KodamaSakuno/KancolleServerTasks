using ServerAPI.Configs;
using ServerAPI.Middlewares;
using ServerAPI.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var databaseConfig = new DatabaseConfig();
builder.Configuration.GetSection("Database").Bind(databaseConfig);
builder.Services.AddSingleton(databaseConfig);
builder.Services.AddScoped<DatabaseService>();

builder.Services.AddTransient<MasterDataService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDatabaseTransaction();

app.MapGet("/ping", () => "Pong");
app.MapControllers();

app.Run();
