using BertCut.Core.Picking;

namespace BertCut.Harness;

/// <summary>
/// The file picker a scripted run gets: it answers from the script and puts nothing on screen.
/// </summary>
/// <remarks>
/// A real picker — the Windows dialog, or BertBrowser — belongs to the desktop and would
/// appear over whatever the user is doing, wherever the window under test is parked. This one
/// records what was asked and returns what <c>pose-pick</c> said to, so the code on both sides
/// of the question runs exactly as it does in the app. With nothing posed it backs out, as a
/// user pressing Cancel would.
/// </remarks>
internal sealed class FakePicker : IFilePicker
{
    private string? _answer;

    /// <summary>Whether <c>pose-pick</c> has supplied an answer that nothing has asked for yet.</summary>
    public bool IsPosed { get; private set; }

    /// <summary>The most recent question, or null when nothing has asked.</summary>
    public PickRequest? Last { get; private set; }

    /// <summary>What the next question will be answered with; null backs out of it.</summary>
    public void Pose(string? answer)
    {
        _answer = answer;
        IsPosed = true;
    }

    public Task<string?> PickAsync(PickRequest request, CancellationToken cancellation = default)
    {
        Last = request;

        var answer = _answer;
        _answer = null;
        IsPosed = false;

        return Task.FromResult(answer);
    }
}
