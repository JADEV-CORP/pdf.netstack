using PdfNetStack.Objects;

namespace PdfNetStack.Tests;

/// <summary>Filling a form by name in one call, and typed field lookup, against the ReportLab fixture.</summary>
[TestClass]
public sealed class FormFillTests
{
    private static PdfDocument Fixture() => PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Forms", "form.pdf"));

    private static PdfDocument Reopen(PdfDocument doc) => PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Optimize = false }));

    [TestMethod]
    public void FillSetsEveryKindByName()
    {
        PdfDocument doc = Fixture();
        doc.Form.Fill(new Dictionary<string, string>
        {
            ["name"] = "Ada Lovelace",
            ["code"] = "12345678",
            ["subscribe"] = "YES",
            ["plan"] = "team",
            ["country"] = "Morocco",
        });

        PdfAcroForm form = Reopen(doc).Form;
        Assert.AreEqual("Ada Lovelace", form["name"]!.Value);
        Assert.AreEqual("123456", form["code"]!.Value, "cut to the field's maximum length");
        Assert.IsTrue(((PdfCheckBoxField)form["subscribe"]!).Checked);
        Assert.AreEqual("team", form["plan"]!.Value);
        Assert.AreEqual("Morocco", ((PdfChoiceField)form["country"]!).SelectedValues.Single());
        Assert.AreEqual("someone@example.com", form["email"]!.Value, "fields not named keep their value");
    }

    [TestMethod]
    public void FillClearsWithEmptyOrNullValues()
    {
        PdfDocument doc = Fixture();
        doc.Form.Fill(new Dictionary<string, string> { ["subscribe"] = "on" });
        Assert.IsTrue(((PdfCheckBoxField)doc.Form["subscribe"]!).Checked);

        doc.Form.Fill(new Dictionary<string, string> { ["subscribe"] = "0", ["plan"] = "", ["email"] = null!, ["country"] = "" });
        Assert.IsFalse(((PdfCheckBoxField)doc.Form["subscribe"]!).Checked);
        Assert.IsNull(doc.Form["plan"]!.Value);
        Assert.IsNull(doc.Form["email"]!.Value);
        Assert.IsEmpty(((PdfChoiceField)doc.Form["country"]!).SelectedValues);
    }

    [TestMethod]
    public void FillReportsEveryProblemAndChangesNothing()
    {
        PdfDocument doc = Fixture();
        var e = Assert.ThrowsExactly<ArgumentException>(() => doc.Form.Fill(new Dictionary<string, string>
        {
            ["name"] = "Changed",
            ["nmae"] = "typo",
            ["plan"] = "enterprise",
            ["subscribe"] = "maybe",
            ["missing.field"] = "x",
        }));

        Assert.AreEqual("values", e.ParamName);
        StringAssert.Contains(e.Message, "no field is named 'nmae'");
        StringAssert.Contains(e.Message, "no field is named 'missing.field'");
        StringAssert.Contains(e.Message, "the field 'plan' does not take the value 'enterprise'");
        StringAssert.Contains(e.Message, "the field 'subscribe' does not take the value 'maybe'");
        Assert.AreNotEqual("Changed", doc.Form["name"]!.Value, "nothing is set when anything is wrong");
    }

    [TestMethod]
    public void FillRefusesFieldsThatHoldNoValueOrAreReadOnly()
    {
        PdfDocument doc = PdfDocument.Create();
        PdfPage page = doc.Pages.Add(PdfRectangle.A4);
        doc.Form.AddTextField(page, "locked", 72, 72, 100, 20, "fixed", new PdfFieldOptions { ReadOnly = true });
        doc.Form.AddSignatureField(page, "sign", 72, 120, 100, 40);
        doc.Form.AddPushButton(page, "go", 72, 200, 60, 20, "Go");

        var e = Assert.ThrowsExactly<ArgumentException>(() => doc.Form.Fill(new Dictionary<string, string> { ["locked"] = "x", ["sign"] = "x", ["go"] = "x" }));
        StringAssert.Contains(e.Message, "the field 'locked' is read only");
        StringAssert.Contains(e.Message, "the field 'sign' is a Signature field");
        StringAssert.Contains(e.Message, "the field 'go' is a PushButton field");
        Assert.AreEqual("fixed", doc.Form["locked"]!.Value);
    }

    [TestMethod]
    public void FillCanFlattenTheForm()
    {
        PdfDocument doc = Fixture();
        doc.Form.Fill(new Dictionary<string, string> { ["name"] = "Ada Lovelace" }, flatten: true);

        PdfDocument back = Reopen(doc);
        Assert.IsFalse(back.Form.Exists);
        StringAssert.Contains(back.Pages[0].ExtractText(), "Ada Lovelace");
    }

    [TestMethod]
    public void TryGetFieldFindsAFieldOfTheKindAsked()
    {
        PdfAcroForm form = Fixture().Form;

        Assert.IsTrue(form.TryGetField("name", out PdfTextField? name));
        Assert.AreEqual("name", name.Name);
        Assert.IsTrue(form.TryGetField("plan", out PdfField? any));
        Assert.AreEqual(PdfFieldKind.RadioButton, any.Kind);

        Assert.IsFalse(form.TryGetField("name", out PdfCheckBoxField? wrongKind));
        Assert.IsNull(wrongKind);
        Assert.IsFalse(form.TryGetField("nobody", out PdfTextField? missing));
        Assert.IsNull(missing);
        Assert.ThrowsExactly<ArgumentNullException>(() => form.TryGetField(null!, out PdfField? _));
    }

    [TestMethod]
    public void SplitPartsKeepTheirFields()
    {
        PdfDocument doc = Fixture();
        using PdfDocument first = doc.Split().First();
        Assert.AreEqual(doc.Form.Fields.Count(f => f.Widgets.Any(w => w.Page == doc.Pages[0])), first.Form.Fields.Count);
        Assert.IsTrue(first.Form.TryGetField("name", out PdfTextField? _));
        Assert.IsTrue(Reopen(first).Form.Fields.All(f => f.Widgets.All(w => w.Page is not null)));
        Assert.IsInstanceOfType<PdfDictionary>(first.Resolve(first.Catalog["AcroForm"]));
    }
}
