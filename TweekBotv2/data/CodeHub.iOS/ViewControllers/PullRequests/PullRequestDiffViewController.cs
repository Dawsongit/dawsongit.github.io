﻿using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using CodeHub.Core;
using CodeHub.Core.Services;
using CodeHub.iOS.Services;
using CodeHub.iOS.Utilities;
using CodeHub.WebViews;
using Humanizer;
using Newtonsoft.Json;
using ReactiveUI;
using Splat;
using UIKit;
using WebKit;

namespace CodeHub.iOS.ViewControllers.PullRequests
{
    public class PullRequestDiffViewController : BaseWebViewController
    {
        private readonly IApplicationService _applicationService;
        private readonly INetworkActivityService _networkActivityService;
        private readonly IMarkdownService _markdownService;
        private readonly string _username;
        private readonly string _repository;
        private readonly int _pullRequestId;
        private readonly string _path;
        private readonly string _patch;
        private readonly string _commit;

        private readonly ReactiveList<Octokit.PullRequestReviewComment> _comments
             = new ReactiveList<Octokit.PullRequestReviewComment>();

        public PullRequestDiffViewController(
            string username,
            string repository,
            int pullRequestId,
            string path,
            string patch,
            string commit,
            IApplicationService applicationService = null,
            INetworkActivityService networkActivityService = null,
            IMarkdownService markdownService = null)
            : base(false)
        {
            _applicationService = applicationService ?? Locator.Current.GetService<IApplicationService>();
            _networkActivityService = networkActivityService ?? Locator.Current.GetService<INetworkActivityService>();
            _markdownService = markdownService ?? Locator.Current.GetService<IMarkdownService>();
            _username = username;
            _repository = repository;
            _pullRequestId = pullRequestId;
            _path = path;
            _patch = patch;
            _commit = commit;

            Title = string.IsNullOrEmpty(_path) ? "Diff" : System.IO.Path.GetFileName(_path);

            var loadComments = ReactiveCommand.CreateFromTask(
                _ => _applicationService.GitHubClient.PullRequest.ReviewComment.GetAll(_username, _repository, _pullRequestId));

            loadComments
                .ThrownExceptions
                .Select(error => new UserError("Unable to load comments.", error))
                .SelectMany(Interactions.Errors.Handle)
                .Subscribe();

            loadComments
                .Subscribe(comments => _comments.Reset(comments));

            var loadAll = ReactiveCommand.CreateCombined(new[] { loadComments });

            Appearing
                .Take(1)
                .Select(_ => Unit.Default)
                .InvokeReactiveCommand(loadAll);
        }

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            Observable
                .Return(Unit.Default)
                .Merge(_comments.Changed.Select(_ => Unit.Default))
                .Do(_ => Render())
                .Subscribe();
        }

        private void Render()
        {
            var comments = _comments
                .Where(x => string.Equals(x.Path, _path))
                .Select(comment => new DiffCommentModel
                {
                    Id = comment.Id,
                    GroupId = comment.Id,
                    Username = comment.User.Login,
                    AvatarUrl = comment.User.AvatarUrl,
                    LineTo = comment.Position,
                    LineFrom = comment.Position,
                    Body = _markdownService.Convert(comment.Body),
                    Date = comment.CreatedAt.Humanize()
                });

            var diffModel = new DiffModel(
                _patch.Split('\n'),
                comments,
                (int)UIFont.PreferredSubheadline.PointSize);

            var diffView = new DiffWebView { Model = diffModel };
            LoadContent(diffView.GenerateString());
        }

        private class JavascriptComment
        {
            public int PatchLine { get; set; }
            public int FileLine { get; set; }
        }

        private class JavascriptReplyComment
        {
            public int Id { get; set; }
        }

        protected override bool ShouldStartLoad(WKWebView webView, WKNavigationAction navigationAction)
        {
            var url = navigationAction.Request.Url;
            if (url.Scheme.Equals("app"))
            {
                var func = url.Host;
                if (func.Equals("comment"))
                {
                    var commentModel = JsonConvert.DeserializeObject<JavascriptComment>(UrlDecode(url.Fragment));
                    PromptForComment(commentModel);
                }
                else if (func.Equals("reply-to"))
                {
                    var commentModel = JsonConvert.DeserializeObject<JavascriptReplyComment>(UrlDecode(url.Fragment));
                    ShowReplyCommentComposer(commentModel.Id);
                }

                return false;
            }

            return base.ShouldStartLoad(webView, navigationAction);
        }

        private void PromptForComment(JavascriptComment model)
        {
            var title = "Line " + model.PatchLine;
            var sheet = new UIActionSheet(title);
            var addButton = sheet.AddButton("Add Comment");
            var cancelButton = sheet.AddButton("Cancel");
            sheet.CancelButtonIndex = cancelButton;
            sheet.Dismissed += (sender, e) =>
            {
                BeginInvokeOnMainThread(() =>
                {
                    if (e.ButtonIndex == addButton)
                        ShowCommentComposer(model.FileLine);
                });

                sheet.Dispose();
            };

            sheet.ShowInView(this.View);
        }

        private void ShowCommentComposer(int line)
        {
            ShowComposer(async text =>
            {
                var commentOptions = new Octokit.PullRequestReviewCommentCreate(text, _commit, _path, line);
                var comment = await _applicationService.GitHubClient.PullRequest.ReviewComment.Create(
                     _username, _repository, _pullRequestId, commentOptions);
                _comments.Add(comment);
            });
        }

        private void ShowReplyCommentComposer(int replyToId)
        {
            ShowComposer(async text =>
            {
                var commentOptions = new Octokit.PullRequestReviewCommentReplyCreate(text, replyToId);
                var comment = await _applicationService.GitHubClient.PullRequest.ReviewComment.CreateReply(
                     _username, _repository, _pullRequestId, commentOptions);
                _comments.Add(comment);
            });
        }

        private void ShowComposer(Func<string, Task> workFn)
        {
            var composer = new MarkdownComposerViewController();
            composer.PresentAsModal(this, async text =>
            {
                var hud = composer.CreateHud();

                using (UIApplication.SharedApplication.DisableInteraction())
                using (_networkActivityService.ActivateNetwork())
                using (hud.Activate("Commenting..."))
                {
                    try
                    {
                        await workFn(text);
                        composer.DismissViewController(true, null);
                    }
                    catch (Exception e)
                    {
                        AlertDialogService.ShowAlert("Unable to Comment", e.Message);
                    }
                }
            });
        }
    }
}

