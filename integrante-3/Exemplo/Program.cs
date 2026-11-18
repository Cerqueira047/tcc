#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Nexora.Analise;

namespace Nexora.Exemplo;

public sealed record Lote(DateTimeOffset Referencia, Evento[] Eventos);

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Uso: dotnet run --project Exemplo -- exemplo-1050.json");
            return 2;
        }

        // As mesmas opções camelCase são usadas no JSON da API ASP.NET Core.
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        try
        {
            var lote = JsonSerializer.Deserialize<Lote>(File.ReadAllText(args[0]), json);
            if (lote?.Eventos is null || lote.Referencia == default)
                throw new JsonException("Informe referencia e eventos no lote.");
            var motor = new AnalisadorPedidos(TimeSpan.FromMinutes(15));
            var ocorrencias = motor.Analisar(lote.Eventos, lote.Referencia);
            Console.WriteLine(JsonSerializer.Serialize(ocorrencias, json));
            return 0;
        }
        catch (Exception erro) when (erro is IOException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine(erro.Message);
            return 1;
        }
    }
}
