using FluentAssertions;
using Verce.Modules.Customers;

namespace Verce.IntegrationTests.Customers;

public class CustomerDomainTests
{
    [Fact]
    public void Valid_CPF_is_normalized_and_invalid_CPF_is_rejected()
    {
        var customer = new Customer(PersonType.Individual, "Ana Silva", null, "529.982.247-25", null, null, null);
        customer.Document.Should().Be("52998224725");
        Action invalid = () => new Customer(PersonType.Individual, "Ana Silva", null, "111.111.111-11", null, null, null);
        invalid.Should().Throw<ArgumentException>().WithMessage("DOCUMENT_INVALID");
    }

    [Theory]
    [InlineData("73.894.567/0001-22", "73894567000122")]
    [InlineData("73.894.567/0001-23", null)]
    [InlineData("11.111.111/1111-11", null)]
    public void CNPJ_validation_normalizes_valid_documents_and_rejects_invalid_ones(string input, string? expected)
    {
        if (expected is null)
        {
            Action action = () => new Customer(PersonType.Company, "Empresa válida", null, input, null, null, null);
            action.Should().Throw<ArgumentException>().WithMessage("DOCUMENT_INVALID");
            return;
        }

        new Customer(PersonType.Company, "Empresa válida", null, input, null, null, null).Document.Should().Be(expected);
    }

    [Fact]
    public void New_primary_and_shipping_address_atomically_replaces_previous_flags()
    {
        var customer = new Customer(PersonType.Company, "Verce 3D", null, "11222333000181", null, null, null);
        var first = customer.AddAddress("Ateliê", "01001-000", "Rua A", "1", null, "Centro", "São Paulo", "SP", "BR", true, true, null);
        var second = customer.AddAddress("Entrega", "20000-000", "Rua B", "2", null, "Centro", "Rio de Janeiro", "RJ", "BR", true, true, null);
        first.IsPrimary.Should().BeFalse(); first.IsDefaultShipping.Should().BeFalse();
        second.IsPrimary.Should().BeTrue(); second.IsDefaultShipping.Should().BeTrue();
    }

    [Fact]
    public void Soft_delete_removes_document_but_preserves_customer_history()
    {
        var customer = new Customer(PersonType.Individual, "Ana Silva", null, "52998224725", "ana@example.com", null, null);
        customer.SoftDelete(DateTimeOffset.UtcNow);
        customer.IsActive.Should().BeFalse(); customer.Document.Should().BeNull(); customer.Name.Should().Be("Ana Silva");
    }
}
