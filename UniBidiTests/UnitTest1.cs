namespace UniBidiTests;

public class Tests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void TestRegularParenthesis()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BidiMap.GetMirror('('), Is.EqualTo(')'));
            Assert.That(BidiMap.GetMirror(')'), Is.EqualTo('('));
        });
    }
}