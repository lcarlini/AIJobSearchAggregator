using System.Globalization;
using System.Text;

namespace AIJobSearchAggregator;

public sealed class Region
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Summary { get; init; }
    public required string GeoId { get; init; }
    public string? Location { get; init; }
    public int? DistanceMiles { get; init; }
    public string[] SearchLocations { get; init; } = [];
    public bool RemoteOnly { get; init; }
    public string[] Cities { get; init; } = [];

    public bool AcceptsLocation(string? location)
    {
        if (RemoteOnly || Cities.Length == 0)
            return true;
        if (string.IsNullOrWhiteSpace(location))
            return true;

        var city = Fold(location.Split(',')[0]);
        if (city is "brasil" or "brazil" or "remoto" or "remote" or "home office" or "homeoffice")
            return true;

        foreach (var candidate in Cities)
        {
            var name = Fold(candidate);
            if (city == name || city.StartsWith(name + " ", StringComparison.Ordinal) || name.StartsWith(city + " ", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static string Fold(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var chars = decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
        return new string(chars.ToArray()).Replace('-', ' ');
    }
}

public static class RegionCatalog
{
    public static readonly Region GrandeSp = new()
    {
        Key = "grande-sp",
        Name = "Grande São Paulo",
        Summary = "São Paulo e as cidades da região metropolitana, num raio de cerca de 80 km.",
        GeoId = "104746682",
        Location = "São Paulo, São Paulo, Brasil",
        DistanceMiles = 50,
        Cities =
        [
            "São Paulo", "Arujá", "Barueri", "Alphaville", "Tamboré", "Biritiba Mirim", "Caieiras", "Cajamar",
            "Carapicuíba", "Cotia", "Diadema", "Embu", "Embu-Guaçu", "Ferraz de Vasconcelos", "Francisco Morato",
            "Franco da Rocha", "Guararema", "Guarulhos", "Itapecerica da Serra", "Itapevi", "Itaquaquecetuba",
            "Jandira", "Juquitiba", "Mairiporã", "Mauá", "Mogi das Cruzes", "Osasco", "Pirapora do Bom Jesus",
            "Poá", "Ribeirão Pires", "Rio Grande da Serra", "Salesópolis", "Santa Isabel", "Santana de Parnaíba",
            "Santo André", "São Bernardo do Campo", "São Caetano do Sul", "São Lourenço da Serra", "Suzano",
            "Taboão da Serra", "Vargem Grande Paulista"
        ]
    };

    public static readonly Region Sorocaba = new()
    {
        Key = "sorocaba",
        Name = "Região de Sorocaba",
        Summary = "Sorocaba e cidades ao redor: Votorantim, Itu, Salto, Tatuí, Boituva, Porto Feliz e vizinhas.",
        GeoId = "",
        Location = "Sorocaba, SP",
        DistanceMiles = 25,
        SearchLocations = ["Sorocaba, SP", "Itu, SP", "Tatuí, SP"],
        Cities =
        [
            "Sorocaba", "Votorantim", "Araçoiaba da Serra", "Alumínio", "Mairinque", "São Roque", "Ibiúna",
            "Piedade", "Salto de Pirapora", "Pilar do Sul", "Tapiraí", "Boituva", "Iperó", "Capela do Alto",
            "Tatuí", "Cesário Lange", "Quadra", "Porto Feliz", "Itu", "Salto", "Jumirim", "Cerquilho", "Tietê",
            "Laranjal Paulista", "Araçariguama", "Cabreúva", "São Miguel Arcanjo", "Sarapuí"
        ]
    };

    public static readonly Region Remoto = new()
    {
        Key = "remoto",
        Name = "Remoto",
        Summary = "Vagas remotas anunciadas para o Brasil.",
        GeoId = "106057199",
        RemoteOnly = true
    };

    public static readonly IReadOnlyList<Region> All = [GrandeSp, Sorocaba, Remoto];

    public static Region? Find(string? key) =>
        All.FirstOrDefault(region => region.Key == key);

    public static string Join(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return string.Join(',', All.Select(region => region.Key).Where(set.Contains));
    }
}
