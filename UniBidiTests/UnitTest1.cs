namespace UniBidiTests;

using UniBidi;

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

    [Test]
    public void TestCheckOutput()
    {
        UniBidi.BidiResolveString("This is a really easy input");
    }

    [Test]
    public void TestCheckOutput2()
    {
        string inputString = "Wow! זה ממש מסובך! Omg!";
        string visualString = UniBidi.BidiResolveString(inputString);

        Assert.That(inputString, Has.Length.EqualTo(visualString.Length));

        for (int i = 0; i < inputString.Length; ++i) {
            Console.WriteLine($"{i}: {inputString[i]} {visualString[i]}");
        }
    }
}