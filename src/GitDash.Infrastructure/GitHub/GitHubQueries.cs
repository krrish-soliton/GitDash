namespace GitDash.Infrastructure.GitHub;

/// <summary>GraphQL documents. Constant strings only; all input goes through variables.</summary>
internal static class GitHubQueries
{
    public const string Viewer = "query { viewer { login name avatarUrl(size: 96) } }";

    public const string Profile = """
        query {
          viewer {
            login name bio company location url createdAt
            avatarUrl(size: 192)
            followers { totalCount }
            following { totalCount }
            repositories(ownerAffiliations: OWNER) { totalCount }
            openPullRequests: pullRequests(states: OPEN) { totalCount }
            mergedPullRequests: pullRequests(states: MERGED) { totalCount }
            allPullRequests: pullRequests { totalCount }
            contributionsCollection { contributionCalendar { totalContributions } }
            organizations(first: 20) { nodes { login } }
          }
        }
        """;

    public const string Snapshot = """
        query($owner: String!, $name: String!) {
          repository(owner: $owner, name: $name) {
            open: pullRequests(states: OPEN, first: 50, orderBy: {field: UPDATED_AT, direction: DESC}) {
              totalCount
              nodes { ...pr }
            }
            merged: pullRequests(states: MERGED, first: 10, orderBy: {field: UPDATED_AT, direction: DESC}) {
              nodes { ...pr }
            }
          }
        }
        fragment pr on PullRequest {
          id number title url isDraft mergeable state createdAt updatedAt mergedAt bodyText additions deletions changedFiles
          author { login avatarUrl(size: 96) }
          mergedBy { login }
          assignees(first: 20) { nodes { login avatarUrl(size: 96) } }
          reviewRequests(first: 20) { nodes { requestedReviewer { __typename ... on User { login avatarUrl(size: 96) } } } }
          reviews(last: 100) { nodes { author { login } state submittedAt comments { totalCount } } }
          comments(last: 100) { totalCount nodes { author { login } createdAt } }
          commits(last: 1) { totalCount nodes { commit { committedDate statusCheckRollup { state } } } }
          timelineItems(last: 40, itemTypes: [ASSIGNED_EVENT, REVIEW_REQUESTED_EVENT]) {
            nodes {
              __typename
              ... on AssignedEvent { createdAt assignee { ... on User { login } } }
              ... on ReviewRequestedEvent { createdAt requestedReviewer { ... on User { login } } }
            }
          }
        }
        """;

    /// <summary>
    /// One person in one repository. Search strings arrive as variables (built from a validated login),
    /// so nothing user-controlled is ever concatenated into the document.
    /// </summary>
    public const string PersonActivity = """
        query($owner: String!, $name: String!, $raised: String!, $open: String!, $merged: String!, $closed: String!,
              $draft: String!, $commented: String!, $reviewed: String!, $recent: String!) {
          raised: search(query: $raised, type: ISSUE) { issueCount }
          open: search(query: $open, type: ISSUE) { issueCount }
          merged: search(query: $merged, type: ISSUE) { issueCount }
          closed: search(query: $closed, type: ISSUE) { issueCount }
          draft: search(query: $draft, type: ISSUE) { issueCount }
          commented: search(query: $commented, type: ISSUE) { issueCount }
          reviewed: search(query: $reviewed, type: ISSUE) { issueCount }
          authored: search(query: $recent, type: ISSUE, first: 12) {
            nodes { ... on PullRequest { number title url state isDraft createdAt closedAt mergedAt } }
          }
          touched: search(query: $commented, type: ISSUE, first: 10) {
            nodes {
              ... on PullRequest {
                number title url
                comments(last: 30) { nodes { author { login } createdAt body url } }
                reviews(last: 30) { nodes { author { login } state submittedAt body url } }
              }
            }
          }
          repository(owner: $owner, name: $name) {
            defaultBranchRef { name }
            refs(refPrefix: "refs/heads/", first: 100, orderBy: {field: TAG_COMMIT_DATE, direction: DESC}) {
              nodes {
                name
                target { ... on Commit { author { user { login } } } }
                associatedPullRequests(states: [MERGED], first: 1) { totalCount }
              }
            }
          }
        }
        """;

    public const string Details = """
        query($owner: String!, $name: String!, $number: Int!) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              createdAt
              body
              author { login }
              timelineItems(last: 150, itemTypes: [PULL_REQUEST_COMMIT, ISSUE_COMMENT, PULL_REQUEST_REVIEW,
                ASSIGNED_EVENT, UNASSIGNED_EVENT, REVIEW_REQUESTED_EVENT, REVIEW_REQUEST_REMOVED_EVENT,
                REVIEW_DISMISSED_EVENT, READY_FOR_REVIEW_EVENT, CONVERT_TO_DRAFT_EVENT,
                HEAD_REF_FORCE_PUSHED_EVENT, MERGED_EVENT, CLOSED_EVENT, REOPENED_EVENT]) {
                nodes {
                  __typename
                  ... on PullRequestCommit { commit { committedDate messageHeadline author { name user { login } } } }
                  ... on IssueComment { createdAt author { login } bodyText body }
                  ... on PullRequestReview {
                    submittedAt author { login } state bodyText body
                    comments(first: 50) { totalCount nodes { createdAt path body author { login } } }
                  }
                  ... on AssignedEvent { createdAt actor { login } assignee { ... on User { login } } }
                  ... on UnassignedEvent { createdAt actor { login } assignee { ... on User { login } } }
                  ... on ReviewRequestedEvent { createdAt actor { login } requestedReviewer { ... on User { login } ... on Team { name } } }
                  ... on ReviewRequestRemovedEvent { createdAt actor { login } requestedReviewer { ... on User { login } ... on Team { name } } }
                  ... on ReviewDismissedEvent { createdAt actor { login } review { author { login } } }
                  ... on ReadyForReviewEvent { createdAt actor { login } }
                  ... on ConvertToDraftEvent { createdAt actor { login } }
                  ... on HeadRefForcePushedEvent { createdAt actor { login } }
                  ... on MergedEvent { createdAt actor { login } }
                  ... on ClosedEvent { createdAt actor { login } }
                  ... on ReopenedEvent { createdAt actor { login } }
                }
              }
            }
          }
        }
        """;
}
