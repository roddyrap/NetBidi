namespace UniBidiTests;

public class Tests
{
    BidiMap bidiData;

    [SetUp]
    public void Setup()
    {
        bidiData = new();
    }

    [Test]
    public void TestRegularParenthesis()
    {
        Assert.Multiple(() =>
        {
            Assert.That(bidiData.GetMirror('('), Is.EqualTo(')'));
            Assert.That(bidiData.GetMirror(')'), Is.EqualTo('('));
        });
    }
}