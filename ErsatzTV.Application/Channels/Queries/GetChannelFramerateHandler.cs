using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.FFmpeg;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Channels;

public class GetChannelFramerateHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    ILogger<GetChannelFramerateHandler> logger)
    : IRequestHandler<GetChannelFramerate, Option<FrameRate>>
{
    // ── Hardcoded target framerate ────────────────────────────────────────────
    // Original logic picked the MINIMUM framerate above 23fps found in the
    // playout, which caused 29.97fps content to normalize down and tank
    // transcoding speed. We instead always normalize to 50fps so the pipeline
    // runs at a fixed rate regardless of source content framerate.
    private static readonly FrameRate TargetFrameRate = new("50/1");

    public async Task<Option<FrameRate>> Handle(GetChannelFramerate request, CancellationToken cancellationToken)
    {
        try
        {
            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            FFmpegProfile ffmpegProfile = await dbContext.Channels
                .AsNoTracking()
                .Filter(c => c.Number == request.ChannelNumber)
                .Include(c => c.FFmpegProfile)
                .Map(c => c.FFmpegProfile)
                .SingleAsync(cancellationToken);

            if (!request.IgnoreProfile && !ffmpegProfile.NormalizeFramerate)
            {
                return Option<FrameRate>.None;
            }

            logger.LogDebug("Checking frame rates for channel {ChannelNumber}", request.ChannelNumber);

            List<Playout> playouts = await dbContext.Playouts
                .AsNoTracking()
                .Include(p => p.Items)
                    .ThenInclude(pi => pi.MediaItem)
                    .ThenInclude(mi => (mi as Movie).MediaVersions)
                .Include(p => p.Items)
                    .ThenInclude(pi => pi.MediaItem)
                    .ThenInclude(mi => (mi as Episode).MediaVersions)
                .Include(p => p.Items)
                    .ThenInclude(pi => pi.MediaItem)
                    .ThenInclude(mi => (mi as Song).MediaVersions)
                .Include(p => p.Items)
                    .ThenInclude(pi => pi.MediaItem)
                    .ThenInclude(mi => (mi as MusicVideo).MediaVersions)
                .Include(p => p.Items)
                    .ThenInclude(pi => pi.MediaItem)
                    .ThenInclude(mi => (mi as OtherVideo).MediaVersions)
                .Include(p => p.Items)
                    .ThenInclude(pi => pi.MediaItem)
                    .ThenInclude(mi => (mi as Image).MediaVersions)
                .Include(p => p.Items)
                    .ThenInclude(pi => pi.MediaItem)
                    .ThenInclude(mi => (mi as RemoteStream).MediaVersions)
                .Filter(p => p.Channel.Number == request.ChannelNumber)
                .ToListAsync(cancellationToken);

            var frameRates = playouts
                .Map(p => p.Items.Map(i => i.MediaItem.GetHeadVersion()))
                .Flatten()
                .Map(mv => new FrameRate(mv.RFrameRate))
                .ToList();

            var distinct = frameRates.Distinct().ToList();

            if (distinct.Count > 1)
            {
                // Mixed framerates on channel — normalize everything to 50fps.
                // Original code picked MinBy(ParsedFrameRate) which caused
                // 29.97fps sources to drag the whole pipeline down.
                logger.LogInformation(
                    "Normalizing frame rate for channel {ChannelNumber} from {Distinct} to {FrameRate}",
                    request.ChannelNumber,
                    distinct.Map(fr => fr.RFrameRate),
                    TargetFrameRate.RFrameRate);

                return TargetFrameRate;
            }

            if (distinct.Count != 0)
            {
                logger.LogInformation(
                    "All content on channel {ChannelNumber} has the same frame rate of {FrameRate}; will not normalize",
                    request.ChannelNumber,
                    distinct[0].RFrameRate);
            }
            else
            {
                logger.LogInformation(
                    "No content on channel {ChannelNumber} has frame rate information; will not normalize",
                    request.ChannelNumber);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Unexpected error checking frame rates on channel {ChannelNumber}",
                request.ChannelNumber);
        }

        return None;
    }
}
