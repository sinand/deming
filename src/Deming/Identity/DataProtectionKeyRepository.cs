using System.Xml.Linq;
using Deming.Data;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Deming.Identity;

/// <summary>
/// Data Protection key ring in the Deming database (table data_protection_key), so panel
/// login cookies survive restarts and no key files are written next to the binary.
/// IXmlRepository is synchronous by contract.
/// </summary>
public sealed class DataProtectionKeyRepository(DemingDb db) : IXmlRepository
{
    private sealed class Row
    {
        public string Xml { get; set; } = "";
    }

    public IReadOnlyCollection<XElement> GetAllElements() =>
        db.Sql("DataProtectionKey.List").GetModelListAsync<Row>().GetAwaiter().GetResult()
            .Select(r => XElement.Parse(r.Xml))
            .ToList();

    public void StoreElement(XElement element, string friendlyName) =>
        db.Sql("DataProtectionKey.Insert")
            .AddParameter("FriendlyName", friendlyName)
            .AddParameter("Xml", element.ToString(SaveOptions.DisableFormatting))
            .ExecuteAsync().GetAwaiter().GetResult();
}
