using UsenetSharp.Models;

namespace UsenetSharpTest.Models;

[TestFixture]
public class UsenetDecodedBodyBatchTests
{
    [Test]
    public void DefaultConstruction_CompletionIsAlreadyCompleted()
    {
        var batch = new UsenetDecodedBodyBatch
        {
            Responses = Array.Empty<Task<UsenetDecodedBodyResponse>>(),
        };

        Assert.That(batch.Completion.IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public async Task InitializerAssignedCompletion_IsExposedReadOnlyToConsumers()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = new UsenetDecodedBodyBatch
        {
            Responses = Array.Empty<Task<UsenetDecodedBodyResponse>>(),
            Completion = completion.Task,
        };

        Assert.That(batch.Completion.IsCompleted, Is.False);
        completion.SetResult();
        await batch.Completion;
        Assert.That(batch.Completion.IsCompletedSuccessfully, Is.True);
    }
}
