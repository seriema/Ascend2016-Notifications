﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Notification;
using EPiServer.PlugIn;
using EPiServer.Scheduler;
using EPiServer.ServiceLocation;
using EPiServer.Web.Routing;
using Tweetinvi;
using Tweetinvi.Core.Interfaces;

namespace Ascend2016.Business.Twitter
{
    [ScheduledPlugIn(DisplayName = "Twitter Search", IntervalLength = 10, IntervalType = ScheduledIntervalType.Seconds)]
    public class TwitterSearchJob : ScheduledJobBase
    {
        private static Dictionary<string, IEnumerable<ITweet>> twitterCache = new Dictionary<string, IEnumerable<ITweet>>();

        private Injected<INotifier> _notifier;
        private bool _stopSignaled;

        public TwitterSearchJob()
        {
            IsStoppable = true;

            var consumerKey = System.Configuration.ConfigurationManager.AppSettings["TwitterConsumerKey"];
            var consumerSecret = System.Configuration.ConfigurationManager.AppSettings["TwitterConsumerSecret"];

            // If you do not already have a BearerToken, use the TRUE parameter to automatically generate it.
            // Note that this will result in a WebRequest to be performed and you will therefore need to make this code safe
            var appCreds = Auth.SetApplicationOnlyCredentials(consumerKey, consumerSecret, true);
            // This method execute the required webrequest to set the bearer Token
            Auth.InitializeApplicationOnlyCredentials(appCreds);
        }

        /// <summary>
        /// Called when a user clicks on Stop for a manually started job, or when ASP.NET shuts down.
        /// </summary>
        public override void Stop()
        {
            _stopSignaled = true;
        }

        /// <summary>
        /// Called when a scheduled job executes
        /// </summary>
        /// <returns>A status message to be stored in the database log and visible from admin mode</returns>
        public override string Execute()
        {
            //Call OnStatusChanged to periodically notify progress of job for manually started jobs
            OnStatusChanged($"Starting execution of {GetType()}");

            //For long running jobs periodically check if stop is signaled and if so stop execution
            if (_stopSignaled)
            {
                return "Stop of job was called";
            }

            //Add implementation
            const int numDaysBacklog = 120;
            var pages = GetLatestPublishedContent(DateTime.Now.AddDays(-numDaysBacklog).ToString(CultureInfo.InvariantCulture)).ToArray();
            if (!pages.Any())
            {
                return $"No pages published in the last {numDaysBacklog} days.";
            }

            var totalShareCount = 0;
            var notificationsCount = 0;

            foreach (var page in pages)
            {
                var url = ExternalUrl(page.ContentLink, CultureInfo.CurrentCulture);
                var tweets = GetTweets(url).ToArray();

                if (!tweets.Any())
                {
                    continue;
                }

                // Cache handling
                var cachedTweets = twitterCache.ContainsKey(url) ? twitterCache[url].ToArray() : new ITweet[0];
                var union = tweets
                    .Union(cachedTweets) // ITweet doesn't support comparison so we have to filter out duplicates ourselves for now.
                    .GroupBy(tweet => tweet.Id)
                    .Select(group => group.First())
                    .ToArray();
                var currentShareCount = CountShares(union);

                totalShareCount += currentShareCount;
                twitterCache[url] = union;

                // Simple notification rule. There should be at least twice as many tweets as the last notification.
                var lastShareCount = CountShares(cachedTweets);
                if (currentShareCount <= lastShareCount*2)
                {
                    continue;
                }

                // Send notification.
                var recipients = new[] { new NotificationUser(page.ChangedBy) };
                CreateNotificationMessage("jojoh", recipients, page, currentShareCount).Wait();
                notificationsCount++;
            }

            return $"Found {pages.Length} pages that were tweeted {totalShareCount} times. Sent {notificationsCount} notifications.";
        }

        private static int CountShares(ITweet[] tweets)
        {
            return tweets.Length + tweets.Sum(x => x.RetweetCount);
        }

        private IEnumerable<ITweet> GetTweets(string url)
        {
            //var tweets = new [] { Tweet.GetTweet(780929654924378112) };
            //var tweets = Search.SearchTweets("https://medium.com/@shemag8/fuck-you-startup-world-ab6cc72fad0e");
            var tweets = Search.SearchTweets(url);

            return tweets;
        }

        private IEnumerable<PageData> GetLatestPublishedContent(string days)
        {
            var criterias = new PropertyCriteriaCollection
            {
                new PropertyCriteria
                {
                    Condition = EPiServer.Filters.CompareCondition.GreaterThan,
                    Name = "PageChanged",
                    Type = PropertyDataType.Date,
                    Value = days,
                    Required = true
                },
                //new PropertyCriteria
                //{
                //    Condition = EPiServer.Filters.CompareCondition.NotEqual,
                //    Name = "ChangedBy",
                //    Type = PropertyDataType.String,
                //    Value = string.Empty,
                //    Required = true
                //}
            };


            var newsPageItems = DataFactory.Instance.FindPagesWithCriteria(PageReference.StartPage, criterias)
                .Where(x => !string.IsNullOrEmpty(x.ChangedBy)); // Can't use PropertyCriteria for this? See my attempt above.
            return newsPageItems;
        }

        private static string ExternalUrl(ContentReference contentLink, CultureInfo language)
        {
            // Borrowed from Henrik: http://stackoverflow.com/a/29934595/703921

            var virtualPathArguments = new VirtualPathArguments {ForceCanonical = true};
            var urlString = UrlResolver.Current.GetUrl(contentLink, language.Name, virtualPathArguments);

            if (string.IsNullOrEmpty(urlString) || HttpContext.Current == null)
            {
                return urlString;
            }

            var uri = new Uri(urlString, UriKind.RelativeOrAbsolute);
            if (uri.IsAbsoluteUri)
            {
                return urlString;
            }

            return new Uri(HttpContext.Current.Request.Url, uri).ToString();
        }

        /// <summary>
        /// Creates and posts the notification message
        /// </summary>
        /// <param name="sender">Author of the message</param>
        /// <param name="recipients">Recipients of the message</param>
        /// <param name="page">The page shared on Twitter</param>
        /// <param name="shareCount">Number of tweets and retweets for <see cref="page"/>page</param>
        /// <returns></returns>
        private async Task CreateNotificationMessage(string sender, IEnumerable<INotificationUser> recipients, PageData page, int shareCount)
        {
            var notificationMessage = new NotificationMessage
            {
                ChannelName = TwitterNotificationFormatter.ChannelName,
                Subject = $@"Your article ""{page.Name}"" is going viral!",
                TypeName = "Tweet",
                Category = new Uri("twitter://myhost/"),
                Sender = new NotificationUser(sender),
                Recipients = recipients,
                Content = $"Your article has {shareCount} tweets and retweets!"
            };
            await _notifier.Service.PostNotificationAsync(notificationMessage).ConfigureAwait(false);
        }
    }
}
