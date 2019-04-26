using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using PopForums.Models;
using PopForums.Repositories;
using PopForums.Services;

namespace PopForums.Sql.Repositories
{
	public class SearchRepository : ISearchRepository
	{
		public SearchRepository(ISqlObjectFactory sqlObjectFactory)
		{
			_sqlObjectFactory = sqlObjectFactory;
		}

		private readonly ISqlObjectFactory _sqlObjectFactory;

		public virtual List<string> GetJunkWords()
		{
			List<string> words = null;
			_sqlObjectFactory.GetConnection().Using(connection =>
				words = connection.Query<string>("SELECT JunkWord FROM pf_JunkWords ORDER BY JunkWord").ToList());
			return words;
		}

		public virtual void CreateJunkWord(string word)
		{
			var exists = false;
			_sqlObjectFactory.GetConnection().Using(connection => 
				exists = connection.Query<string>("SELECT JunkWord FROM pf_JunkWords WHERE JunkWord LIKE @JunkWord", new { JunkWord = word }).Any());
			if (exists)
				return;
			_sqlObjectFactory.GetConnection().Using(connection =>
				connection.Execute("INSERT INTO pf_JunkWords (JunkWord) VALUES (@JunkWord)", new { JunkWord = word }));
		}

		public virtual void DeleteJunkWord(string word)
		{
			_sqlObjectFactory.GetConnection().Using(connection =>
				connection.Execute("DELETE FROM pf_JunkWords WHERE JunkWord = @JunkWord", new { JunkWord = word }));
		}

		public virtual void DeleteAllIndexedWordsForTopic(int topicID)
		{
			_sqlObjectFactory.GetConnection().Using(connection =>
				connection.Execute("DELETE FROM pf_TopicSearchWords WHERE TopicID = @TopicID", new { TopicID = topicID }));
		}

		public virtual void SaveSearchWord(int topicID, string word, int rank)
		{
			_sqlObjectFactory.GetConnection().Using(connection =>
				connection.Execute("INSERT INTO pf_TopicSearchWords (SearchWord, TopicID, Rank) VALUES (@SearchWord, @TopicID, @Rank)", new { SearchWord = word, TopicID = topicID, Rank = rank }));
		}

		public virtual Response<List<Topic>> SearchTopics(string searchTerm, List<int> hiddenForums, SearchType searchType, int startRow, int pageSize, out int topicCount)
		{
			topicCount = 0;
			if (searchTerm.Trim() == String.Empty)
				return new Response<List<Topic>>(new List<Topic>());
			var topics = new List<Topic>();
			var wordArray = searchTerm.Split(new [] { ' ' });
			var wordList = new List<string>();
			var junkWords = GetJunkWords();
			var alphaNum = SearchService.SearchWordPattern;
			for (var x = 0; x < wordArray.Length; x++)
			{
				foreach (Match match in alphaNum.Matches(wordArray[x]))
				{
					if (!junkWords.Contains(match.Value))
						wordList.Add(match.Value);
				}
			}
			var words = wordList.ToArray();
			var wordParameters = new Dictionary<string, string>();
			var sb = new StringBuilder();
			sb.Append("WITH FirstEntries AS (SELECT ROW_NUMBER() OVER (PARTITION BY pf_Topic.TopicID ORDER BY pf_Topic.LastPostTime DESC) AS GroupRow, ");
			sb.Append(TopicRepository.TopicFields);
			sb.Append(", ((");
			for (var x = 0; x < words.Length; x++)
			{
				sb.Append("q");
				sb.Append(x.ToString());
				sb.Append(".Rank");
				if (x < words.Length - 1) sb.Append("+");
			}
			sb.Append(")/" + words.Length + ") AS CompositeRank FROM pf_Topic ");
			for (int x = 0; x < words.Length; x++)
			{
				string xstring = x.ToString();
				sb.Append(" JOIN (SELECT TOP 10000 TopicID, pf_TopicSearchWords.Rank FROM pf_TopicSearchWords WHERE SearchWord = ");
				var param = "@w" + x;
				wordParameters.Add(param, words[x]);
				sb.Append(param);
				sb.Append(" ORDER BY pf_TopicSearchWords.Rank DESC) AS q");
				sb.Append(xstring);
				sb.Append(" ON pf_Topic.TopicID = q");
				sb.Append(xstring);
				sb.Append(".TopicID");
			}
			sb.Append(" WHERE NOT pf_Topic.IsDeleted = 1");
			if (hiddenForums.Count > 0)
			{
				sb.Append(" AND");
				for (int x = 0; x < hiddenForums.Count; x++)
				{
					if (x > 0) sb.Append(" AND");
					sb.Append(String.Format(" NOT ForumID = {0} ", hiddenForums[x]));
				}
			}

			string orderBy;
			switch (searchType)
			{
				case SearchType.Date:
					orderBy = "LastPostTime DESC";
					break;
				case SearchType.Name:
					orderBy = "StartedByName";
					break;
				case SearchType.Replies:
					orderBy = "ReplyCount DESC";
					break;
				case SearchType.Title:
					orderBy = "Title";
					break;
				default:
					orderBy = "CompositeRank DESC, LastPostTime DESC";
					break;
			}

			sb.Append("),\r\nEntries as (SELECT *,ROW_NUMBER() OVER (ORDER BY ");
			sb.Append(orderBy);
			sb.Append(") AS Row, COUNT(*) OVER () as cnt FROM FirstEntries WHERE GroupRow = 1)\r\nSELECT TopicID, ForumID, Title, ReplyCount, ViewCount, StartedByUserID, StartedByName, LastPostUserID, LastPostName, LastPostTime, IsClosed, IsPinned, IsDeleted, UrlName, AnswerPostID, cnt FROM Entries WHERE Row BETWEEN @StartRow AND @StartRow + @PageSize - 1");

			if (words.Length == 0)
				return new Response<List<Topic>>(new List<Topic>());

			var connection = _sqlObjectFactory.GetConnection();
			var command = connection.Command(_sqlObjectFactory, sb.ToString());
			command.AddParameter(_sqlObjectFactory, "@StartRow", startRow);
			command.AddParameter(_sqlObjectFactory, "@PageSize", pageSize);
			foreach (var item in wordParameters)
				command.AddParameter(_sqlObjectFactory, item.Key, item.Value);
			connection.Open();
			var reader = command.ExecuteReader();

			while (reader.Read())
			{
				var topic = new Topic
				{
					TopicID = reader.GetInt32(0),
					ForumID = reader.GetInt32(1),
					Title = reader.GetString(2),
					ReplyCount = reader.GetInt32(3),
					ViewCount = reader.GetInt32(4),
					StartedByUserID = reader.GetInt32(5),
					StartedByName = reader.GetString(6),
					LastPostUserID = reader.GetInt32(7),
					LastPostName = reader.GetString(8),
					LastPostTime = reader.GetDateTime(9),
					IsClosed = reader.GetBoolean(10),
					IsPinned = reader.GetBoolean(11),
					IsDeleted = reader.GetBoolean(12),
					UrlName = reader.GetString(13),
					AnswerPostID = reader.NullIntDbHelper(14)
				};
				topics.Add(topic);
				topicCount = Convert.ToInt32(reader["cnt"]);
			}
			reader.Dispose();
			connection.Close();
			// simple response since results are from database, not external service like ES or Azure
			return new Response<List<Topic>>(topics);
		}
	}
}