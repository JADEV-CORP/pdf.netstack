#:project ../../src/PdfNetStack/PdfNetStack.csproj
// since: 1.3
using PdfNetStack;

#region fill
using PdfDocument doc = PdfDocument.Open("application.pdf");
doc.Form.Fill(new Dictionary<string, string>
{
    ["name"] = "Jane Doe",
    ["subscribe"] = "yes",                     // check boxes take yes, no, true, false, on, off, 1, 0
    ["plan"] = "pro",                          // a radio group takes the value of one of its buttons
    ["country"] = "Morocco",                   // a choice takes an export value or the text shown
});
doc.Save("application-filled.pdf");
#endregion

#region errors
try
{
    doc.Form.Fill(new Dictionary<string, string> { ["nmae"] = "Jane Doe", ["plan"] = "gold" });
}
catch (ArgumentException e)
{
    Console.WriteLine(e.Message);              // both problems at once; no field changed
}
#endregion

#region try-get
if (doc.Form.TryGetField("name", out PdfTextField? name))
{
    Console.WriteLine($"{name.Name} = {name.Text}");
}
#endregion
