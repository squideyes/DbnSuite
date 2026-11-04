# Keys for the worker and readers

The administrator's key can do anything, including delete the archive, so it stays on the server. The worker gets read-write on the bucket, and every reader gets read-only. A key of their own can also be revoked on its own.

The person does this in the RustFS console; secrets it shows them go straight into `.env` or to the reader, not into this conversation.

1. Open `http://<server>:9001` and sign in with `S3_ROOT_ACCESS_KEY` and `S3_ROOT_SECRET_KEY`, which they read from `.env` themselves.
2. Create two policies. Change `dbn` if they set another `S3_BUCKET`.

   `dbn-readonly`:

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       { "Effect": "Allow", "Action": ["s3:GetBucketLocation", "s3:ListBucket"], "Resource": ["arn:aws:s3:::dbn"] },
       { "Effect": "Allow", "Action": ["s3:GetObject"], "Resource": ["arn:aws:s3:::dbn/*"] }
     ]
   }
   ```

   `dbn-readwrite`:

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       { "Effect": "Allow", "Action": ["s3:GetBucketLocation", "s3:ListBucket", "s3:ListBucketMultipartUploads"], "Resource": ["arn:aws:s3:::dbn"] },
       { "Effect": "Allow", "Action": ["s3:GetObject", "s3:PutObject", "s3:DeleteObject", "s3:AbortMultipartUpload", "s3:ListMultipartUploadParts"], "Resource": ["arn:aws:s3:::dbn/*"] }
     ]
   }
   ```

3. Create a user for the worker with `dbn-readwrite` and give it an access key. They put it in `.env` as `DBNSYNC_ACCESS_KEY` and `DBNSYNC_SECRET_KEY`. Then run `docker compose up -d` and repeat the checks in step 6 of the skill: a mistyped key shows up here, not later.
4. Create a user per reader (each person, notebook server or machine) with `dbn-readonly`, each with an access key. They use it with `dbnf login` or any S3 client, at `http://<server>:9000`, path-style.

Readers are bound by the same Databento licence; sharing an archive with anyone it doesn't cover may breach it. Mention this when someone adds a reader outside their own organisation.
