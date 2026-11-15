using System.Collections.ObjectModel;
using GitDash.Presentation.ViewModels;

namespace GitDash.Tests;

public class RunDayTests
{
    private static RunItemViewModel Run(long id, DateTimeOffset at, double progress = 0.5) =>
        new(id, "CI", "repo · main", "Ann", "#1", "Running", "open", progress, true, false, "0:10", "https://github.com/x", "#22D3EE",
            "10:00", at);

    [Fact]
    public void Groups_runs_by_day_newest_first()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var days = new ObservableCollection<RunDayViewModel>();

        RunDayViewModel.Sync(days, [Run(1, now.AddDays(-2)), Run(2, now), Run(3, now.AddHours(-1))], now);

        Assert.Equal(2, days.Count);
        Assert.Equal([2L, 3L], days[0].Items.Select(i => i.Id));
        Assert.Equal([1L], days[1].Items.Select(i => i.Id));
    }

    [Fact]
    public void Updates_rows_in_place_when_the_same_runs_are_still_there()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var days = new ObservableCollection<RunDayViewModel>();
        RunDayViewModel.Sync(days, [Run(1, now)], now);
        var day = days[0];

        RunDayViewModel.Sync(days, [Run(1, now, progress: 0.9)], now);

        Assert.Same(day, days[0]);
        Assert.Equal(0.9, days[0].Items[0].Progress);
    }
}
