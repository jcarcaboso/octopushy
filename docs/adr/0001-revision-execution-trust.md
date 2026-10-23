# Trust the source revision before releasing execution credentials

Planning already executes repository-selected code with credentials, so apply
approval is too late to authorize that code. Require an explicit operator
decision for the exact loaded revision and repository-source generation before
secret-bearing execution; discovering a revision is not that decision. Keep
trust separate from apply approval, retain it for prepared work at that revision,
and do not infer it from historical runs, branch names or a changed source.
Signed-commit automation and narrower credential grants are separate follow-ups,
not alternatives silently enabled by this first implementation.
