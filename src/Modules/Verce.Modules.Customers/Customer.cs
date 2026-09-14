using System.Text.Json.Serialization;
using Verce.Platform.Audit;
using Verce.SharedKernel.Domain;

namespace Verce.Modules.Customers;

/// <summary>M-S2-005 §52: serialized as its member name, not the numeric ordinal, so the
/// generated OpenAPI/TypeScript contract carries a real string enum instead of a bare
/// <c>integer</c> the frontend would have had to redeclare by hand.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PersonType>))]
public enum PersonType { Individual, Company }

[Auditable]
public sealed class Customer : AggregateRoot
{
    private readonly List<CustomerAddress> _addresses = [];
    private Customer() { }

    public Customer(PersonType personType, string name, string? tradeName, string? document, string? email, string? phone, string? notes)
    {
        PersonType = personType;
        ApplyDetails(name, tradeName, document, email, phone, notes);
    }

    public PersonType PersonType { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? TradeName { get; private set; }
    public string? Document { get; private set; }
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? Notes { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset? DeletedAt { get; private set; }
    public IReadOnlyCollection<CustomerAddress> Addresses => _addresses.AsReadOnly();

    public void Update(PersonType personType, string name, string? tradeName, string? document, string? email, string? phone, string? notes)
    {
        PersonType = personType;
        ApplyDetails(name, tradeName, document, email, phone, notes);
    }

    public CustomerAddress AddAddress(string label, string zipCode, string street, string number, string? complement,
        string district, string city, string state, string? country, bool isPrimary, bool isDefaultShipping, string? notes)
    {
        var address = new CustomerAddress(Id, label, zipCode, street, number, complement, district, city, state, country, isPrimary, isDefaultShipping, notes);
        ApplyAddressFlags(address, isPrimary, isDefaultShipping);
        _addresses.Add(address);
        return address;
    }

    public void UpdateAddress(Guid addressId, string label, string zipCode, string street, string number, string? complement,
        string district, string city, string state, string? country, bool isPrimary, bool isDefaultShipping, string? notes)
    {
        var address = FindAddress(addressId);
        address.Update(label, zipCode, street, number, complement, district, city, state, country, isPrimary, isDefaultShipping, notes);
        ApplyAddressFlags(address, isPrimary, isDefaultShipping);
    }

    public void RemoveAddress(Guid addressId) => _addresses.Remove(FindAddress(addressId));
    public void Deactivate() => IsActive = false;
    public void Activate() => IsActive = true;
    public void SoftDelete(DateTimeOffset deletedAt) { DeletedAt = deletedAt; IsActive = false; Document = null; }

    private void ApplyDetails(string name, string? tradeName, string? document, string? email, string? phone, string? notes)
    {
        if (!Enum.IsDefined(PersonType)) throw new ArgumentException("PERSON_TYPE_INVALID");
        Name = Required(name, 2, 200, nameof(name));
        TradeName = Optional(tradeName, 200);
        Document = CustomerDocument.NormalizeAndValidate(document, PersonType);
        Email = Optional(email, 320);
        Phone = Optional(phone, 50);
        Notes = Optional(notes, 4000);
    }

    private void ApplyAddressFlags(CustomerAddress address, bool primary, bool shipping)
    {
        if (primary) foreach (var item in _addresses.Where(x => x.Id != address.Id)) item.SetPrimary(false);
        if (shipping) foreach (var item in _addresses.Where(x => x.Id != address.Id)) item.SetDefaultShipping(false);
        address.SetPrimary(primary); address.SetDefaultShipping(shipping);
    }
    private CustomerAddress FindAddress(Guid id) => _addresses.SingleOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("ADDRESS_NOT_FOUND");
    internal static string Required(string value, int min, int max, string field)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length < min || trimmed.Length > max) throw new ArgumentException($"{field} is invalid.");
        return trimmed;
    }
    internal static string? Optional(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : throw new ArgumentException("Field is too long.");
}

[Auditable]
public sealed class CustomerAddress : Entity, IOwnedBy<Customer>
{
    private CustomerAddress() { }
    internal CustomerAddress(Guid customerId, string label, string zipCode, string street, string number, string? complement,
        string district, string city, string state, string? country, bool isPrimary, bool isDefaultShipping, string? notes)
    { CustomerId = customerId; Update(label, zipCode, street, number, complement, district, city, state, country, isPrimary, isDefaultShipping, notes); }
    public Guid CustomerId { get; private set; }
    public Guid ParentId => CustomerId;
    public string Label { get; private set; } = string.Empty; public string ZipCode { get; private set; } = string.Empty;
    public string Street { get; private set; } = string.Empty; public string Number { get; private set; } = string.Empty;
    public string? Complement { get; private set; }
    public string District { get; private set; } = string.Empty;
    public string City { get; private set; } = string.Empty; public string State { get; private set; } = string.Empty;
    public string Country { get; private set; } = "BR"; public bool IsPrimary { get; private set; }
    public bool IsDefaultShipping { get; private set; }
    public string? Notes { get; private set; }
    internal void Update(string label, string zipCode, string street, string number, string? complement, string district, string city, string state, string? country, bool isPrimary, bool isDefaultShipping, string? notes)
    { Label = Customer.Required(label, 1, 100, nameof(label)); ZipCode = Customer.Required(zipCode, 1, 20, nameof(zipCode)); Street = Customer.Required(street, 1, 200, nameof(street)); Number = Customer.Required(number, 1, 30, nameof(number)); Complement = Customer.Optional(complement, 200); District = Customer.Required(district, 1, 100, nameof(district)); City = Customer.Required(city, 1, 100, nameof(city)); State = Customer.Required(state, 2, 2, nameof(state)).ToUpperInvariant(); Country = Customer.Required(country ?? "BR", 2, 2, nameof(country)).ToUpperInvariant(); IsPrimary = isPrimary; IsDefaultShipping = isDefaultShipping; Notes = Customer.Optional(notes, 1000); }
    internal void SetPrimary(bool value) => IsPrimary = value; internal void SetDefaultShipping(bool value) => IsDefaultShipping = value;
}

public static class CustomerDocument
{
    public static string? NormalizeAndValidate(string? document, PersonType type)
    {
        if (string.IsNullOrWhiteSpace(document)) return null;
        var digits = new string(document.Where(char.IsDigit).ToArray());
        var expected = type == PersonType.Individual ? 11 : 14;
        if (digits.Length != expected || digits.Distinct().Count() == 1 || !HasValidCheckDigits(digits, type)) throw new ArgumentException("DOCUMENT_INVALID");
        return digits;
    }
    private static bool HasValidCheckDigits(string digits, PersonType type)
    {
        int Digit(int count, int[] weights) => Enumerable.Range(0, count).Sum(i => (digits[i] - '0') * weights[i]) % 11 < 2 ? 0 : 11 - Enumerable.Range(0, count).Sum(i => (digits[i] - '0') * weights[i]) % 11;
        if (type == PersonType.Individual) { var cpfFirst = Digit(9, [10, 9, 8, 7, 6, 5, 4, 3, 2]); var cpfSecond = Digit(10, [11, 10, 9, 8, 7, 6, 5, 4, 3, 2]); return digits[9] - '0' == cpfFirst && digits[10] - '0' == cpfSecond; }
        var cnpjFirst = Digit(12, [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]); var cnpjSecond = Digit(13, [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]); return digits[12] - '0' == cnpjFirst && digits[13] - '0' == cnpjSecond;
    }
}
