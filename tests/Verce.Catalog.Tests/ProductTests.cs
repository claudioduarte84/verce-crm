using FluentAssertions;
using Verce.Modules.Catalog;

namespace Verce.Catalog.Tests;

public class ProductTests
{
    [Fact]
    public void Construction_normalizes_code_and_starts_active_with_an_empty_recipe()
    {
        var product = new Product("mini-vase", "Mini Vaso", "Descrição");
        product.Code.Should().Be("MINI-VASE");
        product.Active.Should().BeTrue();
        product.Recipe.Should().NotBeNull();
        product.Recipe.ProductId.Should().Be(product.Id);
        product.Recipe.OutputQuantity.Should().Be(1);
        product.Recipe.MaterialLines.Should().BeEmpty();
        product.Recipe.AdditionalCostLines.Should().BeEmpty();
    }

    [Fact]
    public void Invalid_code_characters_are_rejected()
    {
        var act = () => new Product("mini vase!", "Mini Vaso", null);
        act.Should().Throw<ArgumentException>().WithMessage("PRODUCT_CODE_INVALID_CHARACTERS");
    }

    [Theory]
    [InlineData("a")] // too short
    [InlineData("")]
    public void Code_length_bounds_are_enforced(string code)
    {
        var act = () => new Product(code, "Mini Vaso", null);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UpdateDetails_changes_name_and_description_without_touching_code_or_recipe()
    {
        var product = new Product("MINI-VASE", "Mini Vaso", null);
        var recipeId = product.Recipe.Id;
        product.UpdateDetails("Mini Vaso Grande", "Nova descrição");
        product.Name.Should().Be("Mini Vaso Grande");
        product.Description.Should().Be("Nova descrição");
        product.Code.Should().Be("MINI-VASE");
        product.Recipe.Id.Should().Be(recipeId);
    }

    [Fact]
    public void Activate_and_deactivate_toggle_state_without_deleting_the_product()
    {
        var product = new Product("MINI-VASE", "Mini Vaso", null);
        product.Deactivate();
        product.Active.Should().BeFalse();
        product.Activate();
        product.Active.Should().BeTrue();
    }
}

public class ProductRecipeTests
{
    private static ProductRecipe RecipeOf(Product product) => product.Recipe;

    [Fact]
    public void UpdateParameters_accepts_null_overrides_meaning_inherit_defaults()
    {
        var recipe = RecipeOf(new Product("MINI-VASE", "Mini Vaso", null));
        recipe.UpdateParameters(null, null, null, null, null, 1, null);
        recipe.WastagePercentOverride.Should().BeNull();
        recipe.LaborMinutes.Should().BeNull();
        recipe.MachineMinutes.Should().BeNull();
    }

    [Fact]
    public void Explicit_zero_wastage_is_preserved_as_zero_not_null()
    {
        var recipe = RecipeOf(new Product("MINI-VASE", "Mini Vaso", null));
        recipe.UpdateParameters(0m, null, null, null, null, 1, null);
        recipe.WastagePercentOverride.Should().Be(0m);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Invalid_wastage_percent_is_rejected(decimal wastage)
    {
        var recipe = RecipeOf(new Product("MINI-VASE", "Mini Vaso", null));
        var act = () => recipe.UpdateParameters(wastage, null, null, null, null, 1, null);
        act.Should().Throw<ArgumentException>().WithMessage("RECIPE_WASTAGE_PERCENT_INVALID");
    }

    [Fact]
    public void Invalid_output_quantity_is_rejected()
    {
        var recipe = RecipeOf(new Product("MINI-VASE", "Mini Vaso", null));
        var act = () => recipe.UpdateParameters(null, null, null, null, null, 0, null);
        act.Should().Throw<ArgumentException>().WithMessage("RECIPE_OUTPUT_QUANTITY_INVALID");
    }

    [Theory]
    [InlineData(-1)]
    public void Negative_labor_minutes_are_rejected(decimal minutes)
    {
        var recipe = RecipeOf(new Product("MINI-VASE", "Mini Vaso", null));
        var act = () => recipe.UpdateParameters(null, minutes, null, null, null, 1, null);
        act.Should().Throw<ArgumentException>().WithMessage("RECIPE_LABOR_MINUTES_INVALID");
    }

    [Theory]
    [InlineData(-1)]
    public void Negative_machine_minutes_are_rejected(decimal minutes)
    {
        var recipe = RecipeOf(new Product("MINI-VASE", "Mini Vaso", null));
        var act = () => recipe.UpdateParameters(null, null, null, minutes, null, 1, null);
        act.Should().Throw<ArgumentException>().WithMessage("RECIPE_MACHINE_MINUTES_INVALID");
    }

    [Fact]
    public void ReplaceMaterialLines_assigns_deterministic_sort_order_matching_input_order()
    {
        var recipe = RecipeOf(new Product("MINI-VASE", "Mini Vaso", null));
        var supplyA = Guid.NewGuid();
        var supplyB = Guid.NewGuid();
        var lineA = new ProductRecipeMaterialLine(recipe.Id, supplyA, 10m, "Gram", 10m, null, null);
        var lineB = new ProductRecipeMaterialLine(recipe.Id, supplyB, 20m, "Gram", 20m, null, null);
        recipe.ReplaceMaterialLines([lineA, lineB]);
        recipe.MaterialLines.Should().HaveCount(2);
        recipe.MaterialLines[0].SortOrder.Should().Be(0);
        recipe.MaterialLines[1].SortOrder.Should().Be(1);
    }

    [Fact]
    public void Duplicate_Supply_lines_remain_independent_matching_S4_CostEngine_behavior()
    {
        var recipe = RecipeOf(new Product("MINI-VASE", "Mini Vaso", null));
        var supply = Guid.NewGuid();
        var lineA = new ProductRecipeMaterialLine(recipe.Id, supply, 10m, "Gram", 10m, wastagePercentOverride: 5m, manualUnitCostOverride: null);
        var lineB = new ProductRecipeMaterialLine(recipe.Id, supply, 20m, "Gram", 20m, wastagePercentOverride: 10m, manualUnitCostOverride: null);
        recipe.ReplaceMaterialLines([lineA, lineB]);
        recipe.MaterialLines.Should().HaveCount(2);
        recipe.MaterialLines.Select(l => l.SupplyId).Should().AllBeEquivalentTo(supply);
        recipe.MaterialLines.Select(l => l.WastagePercentOverride).Should().BeEquivalentTo(new decimal?[] { 5m, 10m });
    }

    [Fact]
    public void ReplaceMaterialLines_clears_previous_lines()
    {
        var recipe = RecipeOf(new Product("MINI-VASE", "Mini Vaso", null));
        var line = new ProductRecipeMaterialLine(recipe.Id, Guid.NewGuid(), 10m, "Gram", 10m, null, null);
        recipe.ReplaceMaterialLines([line]);
        recipe.ReplaceMaterialLines([]);
        recipe.MaterialLines.Should().BeEmpty();
    }
}

public class ProductRecipeMaterialLineTests
{
    [Fact]
    public void Empty_supply_id_is_rejected()
    {
        var act = () => new ProductRecipeMaterialLine(Guid.NewGuid(), Guid.Empty, 10m, "Gram", 10m, null, null);
        act.Should().Throw<ArgumentException>().WithMessage("RECIPE_LINE_SUPPLY_REQUIRED");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_entered_quantity_is_rejected(decimal quantity)
    {
        var act = () => new ProductRecipeMaterialLine(Guid.NewGuid(), Guid.NewGuid(), quantity, "Gram", 10m, null, null);
        act.Should().Throw<ArgumentException>().WithMessage("RECIPE_LINE_QUANTITY_MUST_BE_POSITIVE");
    }

    [Fact]
    public void Normalized_quantity_at_or_below_zero_is_rejected()
    {
        var act = () => new ProductRecipeMaterialLine(Guid.NewGuid(), Guid.NewGuid(), 10m, "Gram", 0m, null, null);
        act.Should().Throw<ArgumentException>().WithMessage("RECIPE_LINE_QUANTITY_BELOW_BASE_PRECISION");
    }

    [Fact]
    public void Negative_manual_unit_cost_override_is_rejected()
    {
        var act = () => new ProductRecipeMaterialLine(Guid.NewGuid(), Guid.NewGuid(), 10m, "Gram", 10m, null, -1m);
        act.Should().Throw<ArgumentException>().WithMessage("RECIPE_LINE_MANUAL_COST_INVALID");
    }
}

public class ProductRecipeAdditionalCostLineTests
{
    [Fact]
    public void Negative_amount_is_rejected()
    {
        var act = () => new ProductRecipeAdditionalCostLine(Guid.NewGuid(), "Embalagem", -1m);
        act.Should().Throw<ArgumentException>().WithMessage("RECIPE_ADDITIONAL_COST_AMOUNT_INVALID");
    }

    [Fact]
    public void Zero_amount_is_allowed()
    {
        var line = new ProductRecipeAdditionalCostLine(Guid.NewGuid(), "Embalagem", 0m);
        line.Amount.Should().Be(0m);
    }
}
